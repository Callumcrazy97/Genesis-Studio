#nullable disable
using System.Collections.Generic;

namespace Genesis.Runtime.Scripting.Ast;

// ════════════════════════════════════════════════════════════════════════════
//   PgslAst — typed abstract syntax tree for the PGSL language.
//
//   The existing PgslParser emits VM Instructions directly in a single pass
//   (no intermediate representation). Phase 1 introduces this typed AST as the
//   single front-end output, consumed by two backends:
//     • PgslToCSharpTranspiler  (release: AST → C# → Roslyn → native EntityBehavior)
//     • the typed-stack VM      (dev:    AST → bytecode, instant hot-reload)
//   and by PgslSemanticChecker (edit-time type + command validation).
//
//   Node set mirrors exactly what PgslParser already accepts, so every existing
//   .pgsl script round-trips through the AST without grammar changes.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Base of every AST node. Carries source location for diagnostics.</summary>
public abstract class AstNode
{
    public int Line { get; set; }
    public int Column { get; set; }
}

// ── Top-level ────────────────────────────────────────────────────────────────

/// <summary>A whole PGSL event script (e.g. Fish_Update.pgsl) — a list of statements.</summary>
public sealed class ScriptAst : AstNode
{
    public List<Stmt> Body { get; } = new();
}

// ── Statements ───────────────────────────────────────────────────────────────

public abstract class Stmt : AstNode { }

public sealed class BlockStmt : Stmt
{
    public List<Stmt> Body { get; } = new();
}

public sealed class VarDeclStmt : Stmt
{
    public string Name { get; set; }
    public Expr Initializer { get; set; }   // may be null
}

public sealed class ExprStmt : Stmt
{
    public Expr Expression { get; set; }
}

public sealed class IfStmt : Stmt
{
    public Expr Condition { get; set; }
    public Stmt ThenBranch { get; set; }
    public Stmt ElseBranch { get; set; }     // may be null
}

public sealed class WhileStmt : Stmt
{
    public Expr Condition { get; set; }
    public Stmt Body { get; set; }
}

public sealed class RepeatStmt : Stmt
{
    /// <summary>Number of times to execute the body (GameMaker-style repeat(n)).</summary>
    public Expr Count { get; set; }
    public Stmt Body { get; set; }
}

public sealed class ForStmt : Stmt
{
    public Stmt Initializer { get; set; }    // may be null
    public Expr Condition { get; set; }      // may be null
    public Expr Increment { get; set; }      // may be null
    public Stmt Body { get; set; }
}

/// <summary>GameMaker-style <c>with(expr) { ... }</c> — iterate over instances.</summary>
public sealed class WithStmt : Stmt
{
    public Expr Target { get; set; }
    public Stmt Body { get; set; }
}

/// <summary>Additive command-resolution scope: <c>from Engine.Rendering: ...</c>.</summary>
public sealed class NamespaceBlockStmt : Stmt
{
    public string Namespace { get; set; }
    public BlockStmt Body { get; set; }
}

public sealed class ReturnStmt : Stmt
{
    public Expr Value { get; set; }          // may be null (bare return)
}

public sealed class FunctionDeclStmt : Stmt
{
    public string Name { get; set; }
    public List<string> Parameters { get; } = new();
    public BlockStmt Body { get; set; }
}

// ── Expressions ──────────────────────────────────────────────────────────────

public abstract class Expr : AstNode { }

public sealed class NumberExpr : Expr
{
    public double Value { get; set; }
}

public sealed class StringExpr : Expr
{
    public string Value { get; set; }
}

public sealed class BoolExpr : Expr
{
    public bool Value { get; set; }
}

public sealed class NullExpr : Expr { }

public sealed class IdentifierExpr : Expr
{
    public string Name { get; set; }
}

/// <summary>Binary operator expression: a + b, a == b, a &amp;&amp; b, etc.</summary>
public sealed class BinaryExpr : Expr
{
    public string Op { get; set; }           // "+", "-", "*", "/", "%", "==", "!=", "<", "<=", ">", ">=", "&&", "||", "&", "|", "^", "<<", ">>"
    public Expr Left { get; set; }
    public Expr Right { get; set; }
}

/// <summary>Unary operator expression: -a, !a, ~a.</summary>
public sealed class UnaryExpr : Expr
{
    public string Op { get; set; }           // "-", "!", "~"
    public Expr Operand { get; set; }
}

/// <summary>Assignment: name = value. Also covers compound assignment via <see cref="Op"/>.</summary>
public sealed class AssignExpr : Expr
{
    public string Target { get; set; }       // variable name being assigned
    public string Op { get; set; }           // "=", "+=", "-=", "*=", "/="
    public Expr Value { get; set; }
}

/// <summary>Postfix increment/decrement: i++ / i--.</summary>
public sealed class PostfixExpr : Expr
{
    public string Target { get; set; }
    public string Op { get; set; }           // "++" or "--"
}

/// <summary>Function or command call: Name(arg1, arg2, ...).</summary>
public sealed class CallExpr : Expr
{
    public string Name { get; set; }
    /// <summary>Lexical namespace supplied by a surrounding <c>from Engine.X:</c> block.</summary>
    public string Namespace { get; set; }
    public List<Expr> Arguments { get; } = new();
}

/// <summary>Member access: obj.field (e.g. Engine.Draw). Resolved at transpile time.</summary>
public sealed class MemberExpr : Expr
{
    public Expr Target { get; set; }
    public string Member { get; set; }
}

/// <summary>Index access: arr[i].</summary>
public sealed class IndexExpr : Expr
{
    public Expr Target { get; set; }
    public Expr Index { get; set; }
}

/// <summary>Ternary conditional: cond ? a : b.</summary>
public sealed class TernaryExpr : Expr
{
    public Expr Condition { get; set; }
    public Expr IfTrue { get; set; }
    public Expr IfFalse { get; set; }
}
