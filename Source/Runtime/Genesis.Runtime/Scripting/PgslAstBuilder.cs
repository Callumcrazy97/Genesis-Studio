#nullable disable
using System.Collections.Generic;
using Genesis.Runtime.Scripting.Ast;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting
{
    // ════════════════════════════════════════════════════════════════════════════
    //   PgslAstBuilder
    //   Builds a typed ScriptAst from the existing PgslLexer token stream.
    //   Grammar mirrors PgslParser exactly (same statement/expression precedence),
    //   but emits AST nodes instead of VM Instructions — the foundation for the
    //   PGSL→C# transpiler (release) and the typed-stack VM (dev) sharing one
    //   front-end.
    // ════════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Parses PGSL source into a <see cref="ScriptAst"/>. Reuses <see cref="PgslLexer"/>
    /// so tokenization (including the existing keyword/operator set) is identical to the
    /// VM path. Apply <see cref="PgslCommands.PreProcessScript"/> first to normalise the
    /// Python-style <c>if x:</c> colons and bare-<c>=</c>-in-condition rewrites.
    /// </summary>
    public sealed class PgslAstBuilder
    {
        private readonly List<Token> _tokens;
        private readonly Stack<string> _namespaceStack = new();
        private int _position;

        public PgslAstBuilder(List<Token> tokens)
        {
            _tokens = tokens;
            _position = 0;
        }

        /// <summary>Convenience: lex + build in one call.</summary>
        public static ScriptAst Parse(string source)
        {
            string preprocessed = PgslCommands.PreProcessScript(source);
            var tokens = new PgslLexer(preprocessed).Tokenize();
            return new PgslAstBuilder(tokens).ParseProgram();
        }

        public ScriptAst ParseProgram()
        {
            var script = new ScriptAst();
            while (!IsAtEnd())
            {
                var stmt = Statement();
                if (stmt != null) script.Body.Add(stmt);
            }
            return script;
        }

        // ── Statements ──────────────────────────────────────────────────────────

        private Stmt Statement()
        {
            // Skip stray semicolons.
            while (Match(TokenType.Semicolon)) { }

            if (IsAtEnd()) return null;

            if (Match(TokenType.If))       return IfStatement();
            if (Match(TokenType.While))    return WhileStatement();
            if (Match(TokenType.For))      return ForStatement();
            if (Match(TokenType.Repeat))   return RepeatStatement();
            if (Match(TokenType.With))     return WithStatement();
            if (Match(TokenType.From))     return NamespaceStatement();
            if (Match(TokenType.Return))   return ReturnStatement();
            if (Check(TokenType.Function)) return FunctionDeclaration();
            if (Match(TokenType.LeftBrace)) return Block();

            return ExpressionStatement();
        }

        private BlockStmt Block()
        {
            // Assumes the opening '{' has NOT been consumed; consume it here.
            // (If caller already consumed it via Match, we adapt.)
            var block = new BlockStmt { Line = Peek().Line, Column = Peek().Column };
            while (!IsAtEnd() && !Check(TokenType.RightBrace))
            {
                var s = Statement();
                if (s != null) block.Body.Add(s);
            }
            Consume(TokenType.RightBrace, "Expected '}' to close block.");
            return block;
        }

        private Stmt IfStatement()
        {
            var node = new IfStmt { Line = Previous().Line, Column = Previous().Column };
            // Optional parens (PreProcessScript normalises to parens, but be tolerant).
            bool hadParen = Match(TokenType.LeftParen);
            node.Condition = Expression();
            if (hadParen) Consume(TokenType.RightParen, "Expected ')' after if-condition.");
            Match(TokenType.Colon); // tolerate Python-style trailing colon

            node.ThenBranch = Statement();

            if (Match(TokenType.Else))
            {
                Match(TokenType.Colon);
                node.ElseBranch = Statement();
            }
            return node;
        }

        private Stmt WhileStatement()
        {
            var node = new WhileStmt { Line = Previous().Line, Column = Previous().Column };
            bool hadParen = Match(TokenType.LeftParen);
            node.Condition = Expression();
            if (hadParen) Consume(TokenType.RightParen, "Expected ')' after while-condition.");
            Match(TokenType.Colon);
            node.Body = Statement();
            return node;
        }

        private Stmt RepeatStatement()
        {
            // repeat (count) body   — GameMaker-style
            var node = new RepeatStmt { Line = Previous().Line, Column = Previous().Column };
            bool hadParen = Match(TokenType.LeftParen);
            node.Count = Expression();
            if (hadParen) Consume(TokenType.RightParen, "Expected ')' after repeat-count.");
            Match(TokenType.Colon);
            node.Body = Statement();
            return node;
        }

        private Stmt ForStatement()
        {
            // for (init; cond; incr) body
            var node = new ForStmt { Line = Previous().Line, Column = Previous().Column };
            Consume(TokenType.LeftParen, "Expected '(' after for.");
            if (!Check(TokenType.Semicolon)) node.Initializer = SimpleStatementNoSemi();
            Consume(TokenType.Semicolon, "Expected ';' after for-initializer.");
            if (!Check(TokenType.Semicolon)) node.Condition = Expression();
            Consume(TokenType.Semicolon, "Expected ';' after for-condition.");
            if (!Check(TokenType.RightParen)) node.Increment = Expression();
            Consume(TokenType.RightParen, "Expected ')' after for-clauses.");
            node.Body = Statement();
            return node;
        }

        private Stmt WithStatement()
        {
            var node = new WithStmt { Line = Previous().Line, Column = Previous().Column };
            bool hadParen = Match(TokenType.LeftParen);
            node.Target = Expression();
            if (hadParen) Consume(TokenType.RightParen, "Expected ')' after with-target.");
            Match(TokenType.Colon);
            node.Body = Statement();
            return node;
        }

        private Stmt NamespaceStatement()
        {
            Token start = Previous();
            string commandNamespace = Consume(TokenType.Identifier, "Expected namespace after 'from'.").Value;
            while (Match(TokenType.Dot))
            {
                Token part = Consume(TokenType.Identifier, "Expected namespace segment after '.'.");
                commandNamespace += "." + part.Value;
            }
            Match(TokenType.Colon);

            _namespaceStack.Push(commandNamespace);
            Stmt body;
            try
            {
                body = Statement();
            }
            finally
            {
                _namespaceStack.Pop();
            }

            BlockStmt block = body as BlockStmt ?? new BlockStmt { Line = start.Line, Column = start.Column };
            if (body != null && body is not BlockStmt) block.Body.Add(body);
            return new NamespaceBlockStmt
            {
                Line = start.Line,
                Column = start.Column,
                Namespace = commandNamespace,
                Body = block,
            };
        }

        private Stmt ReturnStatement()
        {
            var node = new ReturnStmt { Line = Previous().Line, Column = Previous().Column };
            if (!Check(TokenType.Semicolon) && !Check(TokenType.RightBrace) && !IsAtEnd())
                node.Value = Expression();
            Match(TokenType.Semicolon);
            return node;
        }

        private Stmt FunctionDeclaration()
        {
            Consume(TokenType.Function, "Expected 'function'.");
            string name = Consume(TokenType.Identifier, "Expected function name.").Value;
            Consume(TokenType.LeftParen, "Expected '(' after function name.");
            var parms = new List<string>();
            if (!Check(TokenType.RightParen))
            {
                do { parms.Add(Consume(TokenType.Identifier, "Expected parameter name.").Value); }
                while (Match(TokenType.Comma));
            }
            Consume(TokenType.RightParen, "Expected ')' after parameters.");
            // function body is a brace block
            Consume(TokenType.LeftBrace, "Expected '{' to start function body.");
            var body = new BlockStmt();
            while (!IsAtEnd() && !Check(TokenType.RightBrace))
            {
                var s = Statement();
                if (s != null) body.Body.Add(s);
            }
            Consume(TokenType.RightBrace, "Expected '}' to close function body.");
            var decl = new FunctionDeclStmt { Line = 0, Column = 0, Name = name, Body = body };
            decl.Parameters.AddRange(parms);
            return decl;
        }

        private Stmt ExpressionStatement() => SimpleStatementNoSemi() is { } e && (MatchSemi() || true) ? e : null;

        private Stmt SimpleStatementNoSemi()
        {
            // var name [= expr]
            if (Match(TokenType.Var))
            {
                var name = Consume(TokenType.Identifier, "Expected variable name after 'var'.").Value;
                Expr init = null;
                if (Match(TokenType.Assign)) init = Expression();
                return new VarDeclStmt { Line = Previous().Line, Name = name, Initializer = init };
            }

            // i++ / i--  (postfix as a statement)
            if (Check(TokenType.Identifier)
                && PeekNext().Type is TokenType.PlusPlus or TokenType.MinusMinus)
            {
                var tok = Advance();
                var opTok = Advance();
                return new ExprStmt
                {
                    Line = tok.Line,
                    Expression = new PostfixExpr { Line = tok.Line, Target = tok.Value, Op = opTok.Value }
                };
            }

            // identifier <compound-assign or => value
            if (Check(TokenType.Identifier))
            {
                var lookahead = PeekNext().Type;
                if (lookahead is TokenType.Assign or TokenType.PlusEqual or TokenType.MinusEqual
                                      or TokenType.MultiplyEqual or TokenType.DivideEqual)
                {
                    var tok = Advance();
                    var opTok = Advance();
                    var value = Expression();
                    return new ExprStmt
                    {
                        Line = tok.Line,
                        Expression = new AssignExpr { Line = tok.Line, Target = tok.Value, Op = opTok.Value, Value = value }
                    };
                }
            }

            // bare expression (e.g. a command call: DrawSprite(...))
            var expr = Expression();
            return new ExprStmt { Line = expr.Line, Expression = expr };
        }

        private bool MatchSemi() => Match(TokenType.Semicolon);

        // ── Expressions (precedence climbing) ───────────────────────────────────

        private Expr Expression() => Assignment();

        private Expr Assignment()
        {
            Expr expr = Ternary();

            // Only treat a bare identifier as an assignment target.
            if (expr is IdentifierExpr id
                && MatchAny(TokenType.Assign, TokenType.PlusEqual, TokenType.MinusEqual,
                            TokenType.MultiplyEqual, TokenType.DivideEqual))
            {
                var opTok = Previous();
                Expr value = Assignment();
                return new AssignExpr { Line = id.Line, Target = id.Name, Op = opTok.Value, Value = value };
            }
            return expr;
        }

        private Expr Ternary()
        {
            Expr cond = LogicalOr();
            if (Match(TokenType.Question))
            {
                var ifTrue = Expression();
                Consume(TokenType.Colon, "Expected ':' in ternary expression.");
                var ifFalse = Expression();
                return new TernaryExpr { Line = cond.Line, Condition = cond, IfTrue = ifTrue, IfFalse = ifFalse };
            }
            return cond;
        }

        private Expr LogicalOr()
        {
            var left = LogicalAnd();
            while (Match(TokenType.Or))
            {
                var op = Previous();
                var right = LogicalAnd();
                left = new BinaryExpr { Line = op.Line, Op = "||", Left = left, Right = right };
            }
            return left;
        }

        private Expr LogicalAnd()
        {
            var left = BitwiseOr();
            while (Match(TokenType.And))
            {
                var op = Previous();
                var right = BitwiseOr();
                left = new BinaryExpr { Line = op.Line, Op = "&&", Left = left, Right = right };
            }
            return left;
        }

        private Expr BitwiseOr()
        {
            var left = BitwiseXor();
            while (Match(TokenType.Pipe))
            {
                var op = Previous();
                var right = BitwiseXor();
                left = new BinaryExpr { Line = op.Line, Op = "|", Left = left, Right = right };
            }
            return left;
        }

        private Expr BitwiseXor()
        {
            var left = BitwiseAnd();
            while (Match(TokenType.Caret))
            {
                var op = Previous();
                var right = BitwiseAnd();
                left = new BinaryExpr { Line = op.Line, Op = "^", Left = left, Right = right };
            }
            return left;
        }

        private Expr BitwiseAnd()
        {
            var left = Equality();
            while (Match(TokenType.Ampersand))
            {
                var op = Previous();
                var right = Equality();
                left = new BinaryExpr { Line = op.Line, Op = "&", Left = left, Right = right };
            }
            return left;
        }

        private Expr Equality()
        {
            var left = Comparison();
            while (MatchAny(TokenType.Equal, TokenType.NotEqual))
            {
                var op = Previous();
                var right = Comparison();
                left = new BinaryExpr { Line = op.Line, Op = op.Value, Left = left, Right = right };
            }
            return left;
        }

        private Expr Comparison()
        {
            var left = Shift();
            while (MatchAny(TokenType.LessThan, TokenType.LessThanOrEqual, TokenType.GreaterThan, TokenType.GreaterThanOrEqual))
            {
                var op = Previous();
                var right = Shift();
                left = new BinaryExpr { Line = op.Line, Op = op.Value, Left = left, Right = right };
            }
            return left;
        }

        private Expr Shift()
        {
            var left = Addition();
            while (MatchAny(TokenType.ShiftLeft, TokenType.ShiftRight))
            {
                var op = Previous();
                var right = Addition();
                left = new BinaryExpr { Line = op.Line, Op = op.Value, Left = left, Right = right };
            }
            return left;
        }

        private Expr Addition()
        {
            var left = Multiplication();
            while (MatchAny(TokenType.Plus, TokenType.Minus))
            {
                var op = Previous();
                var right = Multiplication();
                left = new BinaryExpr { Line = op.Line, Op = op.Value, Left = left, Right = right };
            }
            return left;
        }

        private Expr Multiplication()
        {
            var left = Unary();
            while (MatchAny(TokenType.Multiply, TokenType.Divide, TokenType.Modulo))
            {
                var op = Previous();
                var right = Unary();
                left = new BinaryExpr { Line = op.Line, Op = op.Value, Left = left, Right = right };
            }
            return left;
        }

        private Expr Unary()
        {
            if (MatchAny(TokenType.Not, TokenType.Minus, TokenType.Tilde))
            {
                var op = Previous();
                var operand = Unary();
                return new UnaryExpr { Line = op.Line, Op = op.Value, Operand = operand };
            }
            return Postfix();
        }

        private Expr Postfix()
        {
            var expr = Primary();
            // member access (e.g. Engine.Draw) and indexing chain
            while (true)
            {
                if (Match(TokenType.Dot))
                {
                    var member = Consume(TokenType.Identifier, "Expected member name after '.'.");
                    expr = new MemberExpr { Line = member.Line, Target = expr, Member = member.Value };
                }
                else if (Match(TokenType.LeftParen))
                {
                    string callable = FlattenCallableName(expr);
                    var call = new CallExpr { Line = expr.Line, Name = callable };
                    if (!Check(TokenType.RightParen))
                    {
                        do { call.Arguments.Add(Expression()); }
                        while (Match(TokenType.Comma));
                    }
                    Consume(TokenType.RightParen, "Expected ')' after arguments.");
                    expr = call;
                }
                else if (Match(TokenType.LeftBracket))
                {
                    var idx = Expression();
                    Consume(TokenType.RightBracket, "Expected ']' after index.");
                    expr = new IndexExpr { Line = idx.Line, Target = expr, Index = idx };
                }
                else break;
            }
            return expr;
        }

        private Expr Primary()
        {
            if (Match(TokenType.Number))
            {
                var tok = Previous();
                return new NumberExpr { Line = tok.Line, Value = double.Parse(tok.Value, System.Globalization.CultureInfo.InvariantCulture) };
            }
            if (Match(TokenType.String))
            {
                var tok = Previous();
                return new StringExpr { Line = tok.Line, Value = tok.Value };
            }
            if (Match(TokenType.True))  return new BoolExpr { Line = Previous().Line, Value = true };
            if (Match(TokenType.False)) return new BoolExpr { Line = Previous().Line, Value = false };

            if (Match(TokenType.LeftParen))
            {
                var inner = Expression();
                Consume(TokenType.RightParen, "Expected ')' after expression.");
                return inner;
            }

            if (Check(TokenType.Identifier))
            {
                var tok = Advance();
                // function/command call?
                if (Match(TokenType.LeftParen))
                {
                    var call = new CallExpr
                    {
                        Line = tok.Line,
                        Name = tok.Value,
                        Namespace = _namespaceStack.Count > 0 ? _namespaceStack.Peek() : string.Empty,
                    };
                    if (!Check(TokenType.RightParen))
                    {
                        do { call.Arguments.Add(Expression()); }
                        while (Match(TokenType.Comma));
                    }
                    Consume(TokenType.RightParen, "Expected ')' after arguments.");
                    return call;
                }
                return new IdentifierExpr { Line = tok.Line, Name = tok.Value };
            }

            throw new System.Exception($"Unexpected token '{Peek().Value}' at line {Peek().Line}:{Peek().Column}.");
        }


        private static string FlattenCallableName(Expr expression)
        {
            if (expression is IdentifierExpr identifier) return identifier.Name;
            if (expression is MemberExpr member)
                return FlattenCallableName(member.Target) + "." + member.Member;
            throw new System.Exception("Only identifier/member expressions can be called.");
        }

        // ── Token helpers ───────────────────────────────────────────────────────

        private Token Peek() => _tokens[_position];
        private Token Previous() => _tokens[_position - 1];
        private Token PeekNext() => _position + 1 < _tokens.Count ? _tokens[_position + 1] : _tokens[_position];
        private bool IsAtEnd() => Peek().Type == TokenType.EOF;

        private bool Check(TokenType type) => Peek().Type == type;
        private Token Advance() { if (!IsAtEnd()) _position++; return _tokens[_position - 1]; }

        private bool Match(TokenType type) { if (Check(type)) { Advance(); return true; } return false; }
        private bool MatchAny(params TokenType[] types)
        {
            foreach (var t in types) if (Check(t)) { Advance(); return true; }
            return false;
        }

        private Token Consume(TokenType type, string message)
        {
            if (Check(type)) return Advance();
            throw new System.Exception($"{message} (found '{Peek().Value}' at line {Peek().Line}:{Peek().Column}).");
        }
    }
}
