#nullable disable
using System;
using System.Collections.Generic;

namespace Genesis.Runtime.Scripting.VM;

public class PgslParser
{
    private readonly List<Token> _tokens;
    private readonly IReadOnlyDictionary<string, int> _nativeIdMap;
    private int _position = 0;
    private List<Instruction> _instructions = new();
    private List<object> _constants = new();
    private Dictionary<string, int> _labels = new();
    private List<(string name, int position)> _labelReferences = new();
    private int _nextLabelId;
    private Dictionary<string, UserFunction> _userFunctions = new();
    private readonly Stack<string> _namespaceStack = new();
    // Names declared with `var` (and function parameters): inside a with-block these stay the
    // caller's, and every other variable belongs to the instance the block runs as.
    private HashSet<string> _declaredLocals = new(StringComparer.Ordinal);
    private int _withDepth;
    internal const string InstanceVariablePrefix = "@inst.";
    // Inside a function, its parameters and `var` locals are its own even when they share a
    // built-in's name (x, y, speed...): they live in the function's frame, never the instance.
    internal const string FunctionLocalPrefix = "@fn.";
    private int _functionDepth;
    // The loops being compiled, innermost on top: where break and continue jump, and how deep in
    // with-blocks the loop began (a jump out of a with-block would leave the wrong instance active).
    private Stack<(string Break, string Continue, int WithDepth)> _loops = new();
    // Functions this script declares: a call to one of them is the script's, even when a built-in
    // command has the same name.
    private HashSet<string> _declaredFunctions = new(StringComparer.OrdinalIgnoreCase);

    public PgslParser(List<Token> tokens, IReadOnlyDictionary<string, int> nativeIdMap = null)
    {
        _tokens = tokens;
        _nativeIdMap = nativeIdMap;
        _position = 0;
        _instructions = new List<Instruction>();
        _constants = new List<object>();
        _labels = new Dictionary<string, int>();
        _labelReferences = new List<(string, int)>();
        _nextLabelId = 0;
    }

    // Emit a variable load — uses LOAD_REG (slot-indexed) for known instance registers,
    // falls back to LOAD_VAR (string-keyed) for locals and unknown names.
    // `self.name` is the variable of the instance the code runs as, also inside a function (where a
    // plain assignment makes a local of the function).
    private static string SelfScoped(string name)
    {
        if (name.Length <= 5 || !name.StartsWith("self.", StringComparison.OrdinalIgnoreCase)) return null;
        string member = name.Substring(5);
        return PgslRegisterFile.Slots.ContainsKey(member) ? member : InstanceVariablePrefix + member;
    }

    private string Scoped(string name) =>
        SelfScoped(name) is { } self ? self :
        _functionDepth > 0 && !name.StartsWith("@") && _declaredLocals.Contains(name)
            ? FunctionLocalPrefix + name
            : WithScoped(name);

    private string WithScoped(string name) =>
        _withDepth > 0 && !name.StartsWith("@") && !PgslRegisterFile.Slots.ContainsKey(name)
        && !_declaredLocals.Contains(name) && !name.StartsWith("argument", StringComparison.Ordinal)
            ? InstanceVariablePrefix + name
            : name;

    private void DeclareNextLocal()
    {
        if (Check(TokenType.Identifier)) _declaredLocals.Add(Peek().Value);
    }

    private void EmitLoad(string name)
    {
        name = Scoped(name);
        if (!name.StartsWith("@") && PgslRegisterFile.Slots.TryGetValue(name, out int slot))
            Emit(Opcode.LOAD_REG, slot);
        else
            Emit(Opcode.LOAD_VAR, name);
    }

    // Emit a variable store — uses STORE_REG for known instance registers.
    private void EmitStore(string name)
    {
        name = Scoped(name);
        if (!name.StartsWith("@") && PgslRegisterFile.Slots.TryGetValue(name, out int slot))
            Emit(Opcode.STORE_REG, slot);
        else
            Emit(Opcode.STORE_VAR, name);
    }

    public ParseResult Parse()
    {
        _instructions = new List<Instruction>();
        _constants = new List<object>();
        _labels = new Dictionary<string, int>();
        _labelReferences = new List<(string, int)>();
        _userFunctions = new Dictionary<string, UserFunction>();
        _nextLabelId = 0;
        _declaredLocals = new HashSet<string>(StringComparer.Ordinal);
        _withDepth = 0;
        _functionDepth = 0;
        _loops = new Stack<(string, string, int)>();
        _declaredFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < _tokens.Count; i++)
            if (_tokens[i].Type == TokenType.Function && _tokens[i + 1].Type == TokenType.Identifier)
                _declaredFunctions.Add(_tokens[i + 1].Value);

        while (!IsAtEnd())
        {
            ParseStatement();
        }

        ResolveLabels();

        return new ParseResult(_instructions, _constants, _userFunctions);
    }

    private void ParseStatement()
    {
        if (Match(TokenType.If))
        {
            ParseIfStatement();
        }
        else if (Match(TokenType.For))
        {
            ParseForStatement();
        }
        else if (Match(TokenType.While))
        {
            ParseWhileStatement();
        }
        else if (Match(TokenType.Function))
        {
            ParseFunctionDefinition();
        }
        else if (Match(TokenType.Repeat))
        {
            ParseRepeatStatement();
        }
        else if (Match(TokenType.With))
        {
            ParseWithStatement();
        }
        else if (Match(TokenType.From))
        {
            ParseNamespaceStatement();
        }
        else if (Match(TokenType.Return))
        {
            ParseReturnStatement();
        }
        else if (Match(TokenType.Break) || Match(TokenType.Continue))
        {
            ParseLoopJump(Previous());
        }
        else
        {
            ParseExpressionStatement();
        }
    }

    private void ParseLoopJump(Token keyword)
    {
        string word = keyword.Type == TokenType.Break ? "break" : "continue";
        if (_loops.Count == 0)
        {
            var outside = new Exception($"'{word}' is only allowed inside a loop (line {keyword.Line})");
            outside.Data["Line"] = keyword.Line;
            throw outside;
        }
        var loop = _loops.Peek();
        if (loop.WithDepth != _withDepth)
        {
            var through = new Exception($"'{word}' cannot jump out of a 'with' block (line {keyword.Line})");
            through.Data["Line"] = keyword.Line;
            throw through;
        }
        Emit(Opcode.JUMP, keyword.Type == TokenType.Break ? loop.Break : loop.Continue);
        Match(TokenType.Semicolon);
    }

    private void ParseExpressionStatement()
    {
        ParseAssignmentOrExpression(true);
    }

    private void ParseAssignmentOrExpression(bool expectSemicolon)
    {
        // Check for var prefix
        if (Match(TokenType.Var)) DeclareNextLocal();

        if (Check(TokenType.Identifier) && _position + 1 < _tokens.Count && 
            (_tokens[_position + 1].Type == TokenType.PlusPlus || _tokens[_position + 1].Type == TokenType.MinusMinus))
        {
            ParsePostfixIncrement(isStatement: expectSemicolon);
            return;
        }

        int instrMark = _instructions.Count;
        ParseExpression();

        if (Match(TokenType.Assign) || Match(TokenType.PlusEqual) ||
            Match(TokenType.MinusEqual) || Match(TokenType.MultiplyEqual) ||
            Match(TokenType.DivideEqual))
        {
            TokenType op = Previous().Type;
            var lastInstr = _instructions[^1];
            _instructions.RemoveAt(_instructions.Count - 1);

            // Handle both LOAD_VAR (string-keyed) and LOAD_REG (slot-indexed) as assignment targets
            if (lastInstr.Opcode == Opcode.LOAD_VAR || lastInstr.Opcode == Opcode.LOAD_REG)
            {
                string varName = lastInstr.Opcode == Opcode.LOAD_VAR ? (string)lastInstr.Operand : null;
                int regSlot   = lastInstr.Opcode == Opcode.LOAD_REG  ? (int)lastInstr.Operand    : -1;

                if (op != TokenType.Assign)
                {
                    if (regSlot >= 0) Emit(Opcode.LOAD_REG, regSlot); else EmitLoad(varName);
                    ParseExpression();
                    Emit(op switch {
                        TokenType.PlusEqual    => Opcode.ADD,
                        TokenType.MinusEqual   => Opcode.SUB,
                        TokenType.MultiplyEqual=> Opcode.MUL,
                        TokenType.DivideEqual  => Opcode.DIV,
                        _ => throw new Exception("Unknown compound op")
                    });
                }
                else
                {
                    ParseExpression();
                }
                if (regSlot >= 0) Emit(Opcode.STORE_REG, regSlot); else EmitStore(varName);
            }
            else if (lastInstr.Opcode == Opcode.GET_INDEX)
            {
                if (op != TokenType.Assign)
                {
                     throw new Exception("Compound assignment on index not yet supported");
                }
                ParseExpression();
                Emit(Opcode.SET_INDEX);
            }
            else
            {
                throw new Exception($"Invalid assignment target: {lastInstr.Opcode} at line {Previous().Line}");
            }
            
            if (expectSemicolon) Consume(TokenType.Semicolon, $"Expected ';' after assignment (found {Peek().Type}: '{Peek().Value}')");
        }
        else
        {
            if (expectSemicolon) Consume(TokenType.Semicolon, $"Expected ';' after expression (found {Peek().Type}: '{Peek().Value}')");
            Emit(Opcode.POP);
        }
    }

    private void ParseIfStatement()
    {
        bool hasParen = Match(TokenType.LeftParen);
        ParseExpression();
        if (hasParen) 
        {
            if (!Check(TokenType.RightParen))
            {
                // If we don't see a ')', maybe ParseExpression consumed it? 
                // Or maybe it's just missing.
            }
            else
            {
                Consume(TokenType.RightParen, "Expected ')' after condition");
            }
        }

        string elseLabel = GenerateLabel("else");
        string endLabel = GenerateLabel("end");

        Emit(Opcode.JUMP_IF_FALSE, elseLabel);

        ParseBlockOrStatement();

        Emit(Opcode.JUMP, endLabel);
        DefineLabel(elseLabel);

        if (Match(TokenType.Else))
        {
            ParseBlockOrStatement();
        }

        DefineLabel(endLabel);
    }

    private void ParseWhileStatement()
    {
        string startLabel = GenerateLabel("while_start");
        string endLabel = GenerateLabel("while_end");

        bool hasParen = Match(TokenType.LeftParen);
        DefineLabel(startLabel);
        ParseExpression();
        if (hasParen) Consume(TokenType.RightParen, "Expected ')' after condition");
        Emit(Opcode.JUMP_IF_FALSE, endLabel);

        _loops.Push((endLabel, startLabel, _withDepth));
        try { ParseBlockOrStatement(); }
        finally { _loops.Pop(); }
        Emit(Opcode.JUMP, startLabel);

        DefineLabel(endLabel);
    }

    private void ParseForStatement()
    {
        Consume(TokenType.LeftParen, "Expected '(' after 'for'");
        
        // Initializer
        if (Match(TokenType.Var)) DeclareNextLocal();
        if (!Check(TokenType.Semicolon)) ParseStatement();
        else Consume(TokenType.Semicolon, "Expected ';' after for initializer");

        string startLabel = GenerateLabel("for_start");
        string endLabel = GenerateLabel("for_end");
        string bodyLabel = GenerateLabel("for_body");
        string incrementLabel = GenerateLabel("for_inc");

        DefineLabel(startLabel);
        
        // Condition
        if (!Check(TokenType.Semicolon)) {
            ParseExpression();
            Emit(Opcode.JUMP_IF_FALSE, endLabel);
        }
        Consume(TokenType.Semicolon, "Expected ';' after for condition");
        
        Emit(Opcode.JUMP, bodyLabel);

        // Increment
        DefineLabel(incrementLabel);
        if (!Check(TokenType.RightParen)) {
            ParseAssignmentOrExpression(false);
        }
        Consume(TokenType.RightParen, "Expected ')' after for increment");
        Emit(Opcode.JUMP, startLabel);

        // Body
        DefineLabel(bodyLabel);
        _loops.Push((endLabel, incrementLabel, _withDepth));
        try { ParseBlockOrStatement(); }
        finally { _loops.Pop(); }
        Emit(Opcode.JUMP, incrementLabel);

        DefineLabel(endLabel);
    }

    private void ParseBlockOrStatement()
    {
        if (Check(TokenType.LeftBrace))
            ParseBlock();
        else
            ParseStatement();
    }

    private void ParseNamespaceStatement()
    {
        Consume(TokenType.Identifier, "Expected namespace after 'from'");
        string commandNamespace = Previous().Value;
        while (Match(TokenType.Dot))
        {
            Consume(TokenType.Identifier, "Expected namespace segment after '.'");
            commandNamespace += "." + Previous().Value;
        }
        Match(TokenType.Colon);

        _namespaceStack.Push(commandNamespace);
        try
        {
            ParseBlockOrStatement();
        }
        finally
        {
            _namespaceStack.Pop();
        }
    }

    private void ParseReturnStatement()
    {
        if (Peek().Type == TokenType.Semicolon)
        {
            Emit(Opcode.PUSH_NULL);
        }
        else
        {
            ParseExpression();
        }
        Emit(Opcode.RETURN);
        Consume(TokenType.Semicolon, "Expected ';' after return");
    }

    private void ParseRepeatStatement()
    {
        string suffix = GenerateLabel("");
        var countVar = "@repeat_count" + suffix;
        var iVar = "@repeat_i" + suffix;

        Consume(TokenType.LeftParen, "Expected '(' after 'repeat'");
        ParseExpression();
        Consume(TokenType.RightParen, "Expected ')' after repeat count");
        Emit(Opcode.STORE_VAR, countVar);

        Emit(Opcode.LOAD_CONST, AddConstant(0.0));
        Emit(Opcode.STORE_VAR, iVar);

        string startLabel = GenerateLabel("repeat_start");
        string endLabel = GenerateLabel("repeat_end");
        DefineLabel(startLabel);

        Emit(Opcode.LOAD_VAR, iVar);
        Emit(Opcode.LOAD_VAR, countVar);
        Emit(Opcode.GTE);
        Emit(Opcode.JUMP_IF_TRUE, endLabel);

        string nextLabel = GenerateLabel("repeat_next");
        _loops.Push((endLabel, nextLabel, _withDepth));
        try { ParseBlockOrStatement(); }
        finally { _loops.Pop(); }

        DefineLabel(nextLabel);
        Emit(Opcode.LOAD_VAR, iVar);
        Emit(Opcode.LOAD_CONST, AddConstant(1.0));
        Emit(Opcode.ADD);
        Emit(Opcode.STORE_VAR, iVar);

        Emit(Opcode.JUMP, startLabel);
        DefineLabel(endLabel);
    }

    private void ParseWithStatement()
    {
        Consume(TokenType.LeftParen, "Expected '(' after 'with'");
        string bare = Check(TokenType.Identifier) && _position + 1 < _tokens.Count
            && _tokens[_position + 1].Type == TokenType.RightParen
            && !PgslRegisterFile.Slots.ContainsKey(Peek().Value) && !_declaredLocals.Contains(Peek().Value)
                ? Peek().Value : null;
        if (bare != null)
        {
            // `with (obj_Weapon)`: the name of an Object, unless a variable of that name holds an
            // instance id. The VM decides, so pass the name itself.
            Advance();
            Emit(Opcode.LOAD_CONST, AddConstant(bare));
        }
        else
        {
            ParseExpression(); // target
        }
        Consume(TokenType.RightParen, "Expected ')' after with target");

        string startLabel = GenerateLabel("with_start");
        string endLabel = GenerateLabel("with_end");

        Emit(Opcode.WITH_START, bare != null ? 1 : 0);

        DefineLabel(startLabel);
        Emit(Opcode.WITH_NEXT, endLabel);

        _withDepth++;
        try { ParseBlockOrStatement(); }
        finally { _withDepth--; }

        Emit(Opcode.JUMP, startLabel);

        DefineLabel(endLabel);
        Emit(Opcode.WITH_END);
    }

    private void ParseFunctionDefinition()
    {
        Consume(TokenType.Identifier, "Expected function name");
        string funcName = Previous().Value;

        Consume(TokenType.LeftParen, "Expected '(' after function name");
        var parameters = new List<string>();
        if (!Check(TokenType.RightParen))
        {
            do
            {
                Consume(TokenType.Identifier, "Expected parameter name");
                parameters.Add(Previous().Value);
            } while (Match(TokenType.Comma));
        }
        Consume(TokenType.RightParen, "Expected ')' after parameters");

        var userFunc = new UserFunction(funcName, parameters);
        var savedLocals = _declaredLocals;
        int savedWithDepth = _withDepth;
        _declaredLocals = new HashSet<string>(parameters, StringComparer.Ordinal);
        _withDepth = 0;
        _functionDepth++;
        var savedLoops = _loops;
        _loops = new Stack<(string, string, int)>();

        var savedInstructions = _instructions;
        var savedConstants = _constants;
        var savedLabels = _labels;
        var savedLabelReferences = _labelReferences;

        _instructions = new List<Instruction>();
        _constants = new List<object>();
        _labels = new Dictionary<string, int>();
        _labelReferences = new List<(string, int)>();

        ParseBlock();

        ResolveLabels();

        userFunc.Bytecode = new List<Instruction>(_instructions);
        userFunc.Constants = new List<object>(_constants);

        _instructions = savedInstructions;
        _constants = savedConstants;
        _labels = savedLabels;
        _labelReferences = savedLabelReferences;
        _declaredLocals = savedLocals;
        _withDepth = savedWithDepth;
        _loops = savedLoops;
        _functionDepth--;

        _userFunctions[funcName] = userFunc;
    }

    private void ParseBlock()
    {
        Consume(TokenType.LeftBrace, "Expected '{' to start block");
        while (!Check(TokenType.RightBrace) && !IsAtEnd())
        {
            ParseStatement();
        }
        Consume(TokenType.RightBrace, "Expected '}' to end block");
    }

    private void ParseExpression()
    {
        ParseTernary();
    }

    private void ParseTernary()
    {
        ParseLogicalOr();

        if (Match(TokenType.Question))
        {
            var elseLabel = GenerateLabel("ternary_else");
            var endLabel = GenerateLabel("ternary_end");

            Emit(Opcode.JUMP_IF_FALSE, elseLabel);
            ParseExpression();
            Consume(TokenType.Colon, "Expected ':' in ternary expression");
            Emit(Opcode.JUMP, endLabel);
            DefineLabel(elseLabel);
            ParseExpression();
            DefineLabel(endLabel);
        }
    }

    private void ParseLogicalOr()
    {
        ParseLogicalAnd();

        // Short-circuit: when the left side is true, the right side is not evaluated.
        while (Match(TokenType.Or))
        {
            string right = GenerateLabel("or_right");
            string end = GenerateLabel("or_end");
            Emit(Opcode.JUMP_IF_FALSE, right);
            Emit(Opcode.LOAD_CONST, AddConstant(1.0));
            Emit(Opcode.JUMP, end);
            DefineLabel(right);
            ParseLogicalAnd();
            Emit(Opcode.NOT);
            Emit(Opcode.NOT);
            DefineLabel(end);
        }
    }

    private void ParseLogicalAnd()
    {
        ParseBitwiseOr();

        // Short-circuit: when the left side is false, the right side is not evaluated.
        while (Match(TokenType.And))
        {
            string falseLabel = GenerateLabel("and_false");
            string end = GenerateLabel("and_end");
            Emit(Opcode.JUMP_IF_FALSE, falseLabel);
            ParseBitwiseOr();
            Emit(Opcode.NOT);
            Emit(Opcode.NOT);
            Emit(Opcode.JUMP, end);
            DefineLabel(falseLabel);
            Emit(Opcode.LOAD_CONST, AddConstant(0.0));
            DefineLabel(end);
        }
    }

    private void ParseBitwiseOr()
    {
        ParseBitwiseXor();
        while (Match(TokenType.Pipe))
        {
            ParseBitwiseXor();
            Emit(Opcode.BIT_OR);
        }
    }

    private void ParseBitwiseXor()
    {
        ParseBitwiseAnd();
        while (Match(TokenType.Caret))
        {
            ParseBitwiseAnd();
            Emit(Opcode.BIT_XOR);
        }
    }

    private void ParseBitwiseAnd()
    {
        ParseBitwiseShift();
        while (Match(TokenType.Ampersand))
        {
            ParseBitwiseShift();
            Emit(Opcode.BIT_AND);
        }
    }

    private void ParseBitwiseShift()
    {
        ParseEquality();
        while (Match(TokenType.ShiftLeft) || Match(TokenType.ShiftRight))
        {
            TokenType op = Previous().Type;
            ParseEquality();
            Emit(op == TokenType.ShiftLeft ? Opcode.SHL : Opcode.SHR);
        }
    }

    private void ParseEquality()
    {
        ParseComparison();

        while (Match(TokenType.Equal) || Match(TokenType.NotEqual))
        {
            TokenType op = Previous().Type;
            ParseComparison();

            if (op == TokenType.Equal)
                Emit(Opcode.EQ);
            else
                Emit(Opcode.NEQ);
        }
    }

    private void ParseComparison()
    {
        ParseTerm();

        while (Match(TokenType.LessThan) || Match(TokenType.LessThanOrEqual) ||
               Match(TokenType.GreaterThan) || Match(TokenType.GreaterThanOrEqual))
        {
            TokenType op = Previous().Type;
            ParseTerm();

            Emit(op switch
            {
                TokenType.LessThan => Opcode.LT,
                TokenType.LessThanOrEqual => Opcode.LTE,
                TokenType.GreaterThan => Opcode.GT,
                TokenType.GreaterThanOrEqual => Opcode.GTE,
                _ => throw new Exception($"Unknown comparison operator: {op} at line {Peek().Line}")
            });
        }
    }

    private void ParseTerm()
    {
        ParseFactor();

        while (Match(TokenType.Plus) || Match(TokenType.Minus))
        {
            TokenType op = Previous().Type;
            ParseFactor();

            Emit(op == TokenType.Plus ? Opcode.ADD : Opcode.SUB);
        }
    }

    private void ParseFactor()
    {
        ParseUnary();

        while (Match(TokenType.Multiply) || Match(TokenType.Divide) || Match(TokenType.Modulo))
        {
            TokenType op = Previous().Type;
            ParseUnary();

            Emit(op switch
            {
                TokenType.Multiply => Opcode.MUL,
                TokenType.Modulo => Opcode.MOD,
                _ => Opcode.DIV
            });
        }
    }

    private void ParseUnary()
    {
        if (Match(TokenType.Minus))
        {
            ParseUnary();
            Emit(Opcode.NEG);
        }
        else if (Match(TokenType.Not))
        {
            ParseUnary();
            Emit(Opcode.NOT);
        }
        else if (Match(TokenType.Tilde))
        {
            ParseUnary();
            Emit(Opcode.BIT_NOT);
        }
        else
        {
            ParsePrimary();
        }
    }

    private void ParsePrimary()
    {
        if (Match(TokenType.Number))
        {
            double value = double.Parse(Previous().Value);
            int constIndex = AddConstant(value);
            Emit(Opcode.LOAD_CONST, constIndex);
        }
        else if (Match(TokenType.String))
        {
            int constIndex = AddConstant(Previous().Value);
            Emit(Opcode.LOAD_CONST, constIndex);
        }
        else if (Match(TokenType.True))
        {
            int constIndex = AddConstant(1);
            Emit(Opcode.LOAD_CONST, constIndex);
        }
        else if (Match(TokenType.False))
        {
            int constIndex = AddConstant(0);
            Emit(Opcode.LOAD_CONST, constIndex);
        }
        else if (Match(TokenType.Identifier))
        {
            string name = Previous().Value;
            while (Match(TokenType.Dot))
            {
                Consume(TokenType.Identifier, "Expected identifier after '.'");
                name += "." + Previous().Value;
            }
            if (Match(TokenType.LeftParen))
            {
                ParseFunctionCall(name);
            }
            else
            {
                EmitLoad(name);
            }
        }
        else if (Match(TokenType.LeftBracket))
        {
            ParseArrayLiteral();
        }
        else if (Match(TokenType.LeftParen))
        {
            ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
        }
        else
        {
            var ex = new Exception($"Unexpected token: {Peek().Type} ('{Peek().Value}') at line {Peek().Line}");
            ex.Data["Line"] = Peek().Line;
            throw ex;
        }

        // Postfix operators (indexing, ++, --)
        while (Match(TokenType.LeftBracket) || Match(TokenType.PlusPlus) || Match(TokenType.MinusMinus))
        {
            var op = Previous().Type;
            if (op == TokenType.LeftBracket)
            {
                ParseExpression();
                Consume(TokenType.RightBracket, "Expected ']' after index");
                Emit(Opcode.GET_INDEX);
            }
            else if (op == TokenType.PlusPlus || op == TokenType.MinusMinus)
            {
                var ex = new Exception("Postfix ++/-- is only supported as a standalone statement.");
                ex.Data["Line"] = Previous().Line;
                throw ex;
            }
        }
    }

    private void ParseArrayLiteral()
    {
        int argCount = 0;
        if (!Check(TokenType.RightBracket))
        {
            do
            {
                ParseExpression();
                argCount++;
            } while (Match(TokenType.Comma));
        }
        Consume(TokenType.RightBracket, "Expected ']' after array items");
        Emit(Opcode.CALL, ("PgListCreate", argCount));
    }

    private void ParsePostfixIncrement(bool isStatement)
    {
        Match(TokenType.Identifier);
        string varName = Previous().Value;
        TokenType op = Advance().Type;

        EmitLoad(varName);
        int constIdx = AddConstant(1.0);
        Emit(Opcode.LOAD_CONST, constIdx);
        Emit(op == TokenType.PlusPlus ? Opcode.ADD : Opcode.SUB);
        EmitStore(varName);

        if (isStatement)
        {
            Consume(TokenType.Semicolon, "Expected ';' after postfix operator");
        }
    }

    private void ParseFunctionCall(string functionName)
    {
        int argCount = 0;

        if (!Check(TokenType.RightParen))
        {
            do
            {
                ParseExpression();
                argCount++;
            } while (Match(TokenType.Comma));
        }

        Consume(TokenType.RightParen, "Expected ')' after function arguments");

        string dispatchName = functionName;
        if (!functionName.Contains('.') && _namespaceStack.Count > 0)
            dispatchName = Genesis.Runtime.Scripting.PgslNamespaceResolver.Resolve(_namespaceStack.Peek(), functionName);

        // Emit CALL_NATIVE when the command is in the pre-built native table (skips dictionary lookup at runtime).
        // Fall back to CALL for user functions, script assets, and any command not yet in the table.
        if (_nativeIdMap != null && !_declaredFunctions.Contains(functionName) && _nativeIdMap.TryGetValue(dispatchName, out int nativeId))
            Emit(Opcode.CALL_NATIVE, (nativeId, argCount));
        else
            Emit(Opcode.CALL, (dispatchName, argCount));
    }

    private void Emit(Opcode opcode, object operand = null)
    {
        int position = _instructions.Count;
        int line = _position > 0 ? _tokens[_position - 1].Line : 1;
        _instructions.Add(new Instruction(opcode, operand, line));
        if (operand is string labelName && IsJumpOpcode(opcode))
        {
            _labelReferences.Add((labelName, position));
        }
    }

    private static bool IsJumpOpcode(Opcode op) =>
        op == Opcode.JUMP || op == Opcode.JUMP_IF_FALSE ||
        op == Opcode.JUMP_IF_TRUE || op == Opcode.WITH_NEXT;

    private int AddConstant(object value)
    {
        _constants.Add(value);
        return _constants.Count - 1;
    }

    private string GenerateLabel(string prefix)
    {
        // Labels are requested before they are defined. Using _labels.Count made a nested branch
        // reuse its parent's still-undefined names, so defining the inner labels rewired the outer
        // jumps and skipped the rest of the block. A monotonic id is unique at allocation time.
        return $"{prefix}_{_nextLabelId++}";
    }

    private void DefineLabel(string name)
    {
        _labels[name] = _instructions.Count;
    }

    private void ResolveLabels()
    {
        foreach (var (name, position) in _labelReferences)
        {
            if (_labels.TryGetValue(name, out int targetPosition))
            {
                _instructions[position] = new Instruction(_instructions[position].Opcode, targetPosition);
            }
            else
            {
                throw new Exception($"Undefined label: {name}");
            }
        }
    }

    private bool Match(TokenType type)
    {
        if (Check(type))
        {
            Advance();
            return true;
        }
        return false;
    }

    private bool Check(TokenType type)
    {
        if (IsAtEnd()) return false;
        return Peek().Type == type;
    }

    private Token Advance()
    {
        if (!IsAtEnd()) _position++;
        return Previous();
    }

    private bool IsAtEnd()
    {
        return Peek().Type == TokenType.EOF;
    }

    private Token Peek()
    {
        return _tokens[_position];
    }

    private Token Previous()
    {
        return _tokens[_position - 1];
    }

    private Token Consume(TokenType type, string message)
    {
        if (Check(type)) return Advance();
        var ex = new Exception($"{message} at line {Peek().Line}");
        ex.Data["Line"] = Peek().Line;
        throw ex;
    }
}

public class ParseResult
{
    public List<Instruction> Instructions { get; set; }
    public List<object> Constants { get; set; }
    public Dictionary<string, UserFunction> UserFunctions { get; set; }

    public ParseResult(List<Instruction> instructions, List<object> constants, Dictionary<string, UserFunction> userFunctions)
    {
        Instructions = instructions;
        Constants = constants;
        UserFunctions = userFunctions;
    }

    public void Deconstruct(out List<Instruction> instructions, out List<object> constants, out Dictionary<string, UserFunction> userFunctions)
    {
        instructions = Instructions;
        constants = Constants;
        userFunctions = UserFunctions;
    }
}
