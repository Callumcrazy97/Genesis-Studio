#nullable disable
using System;
using System.Collections.Generic;
using System.Text;

namespace Genesis.Runtime.Scripting.VM;

public enum TokenType
{
    Number,
    String,
    Identifier,
    Plus,
    Minus,
    Multiply,
    Divide,
    Modulo,
    Assign,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    Comma,
    Semicolon,
    If,
    Else,
    While,
    Function,
    Return,
    True,
    False,
    And,
    Or,
    Not,
    Question,
    Colon,
    Dot,
    Repeat,
    With,
    From,
    LeftBracket,
    RightBracket,
    For,
    Var,
    PlusPlus,
    MinusMinus,
    PlusEqual,
    MinusEqual,
    MultiplyEqual,
    DivideEqual,
    Ampersand,
    Pipe,
    Caret,
    Tilde,
    ShiftLeft,
    ShiftRight,
    EOF,
    Unknown
}

public class Token
{
    public TokenType Type { get; set; }
    public string Value { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }

    public Token(TokenType type, string value, int line, int column)
    {
        Type = type;
        Value = value;
        Line = line;
        Column = column;
    }

    public override string ToString()
    {
        return $"{Type}('{Value}', {Line}:{Column})";
    }
}

public class PgslLexer
{
    private readonly string _source;
    private int _position;
    private int _line;
    private int _column;

    public PgslLexer(string source)
    {
        _source = source;
        _position = 0;
        _line = 1;
        _column = 1;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (_position < _source.Length)
        {
            char current = Peek();

            if (char.IsWhiteSpace(current))
            {
                SkipWhitespace();
                continue;
            }

            if (current == '/' && PeekNext() == '/')
            {
                SkipComment();
                continue;
            }

            if (char.IsDigit(current) || (current == '.' && char.IsDigit(PeekNext())))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            if (current == '"')
            {
                tokens.Add(ReadString());
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                tokens.Add(ReadIdentifierOrKeyword());
                continue;
            }

            tokens.Add(ReadOperatorOrDelimiter());
        }

        tokens.Add(new Token(TokenType.EOF, "", _line, _column));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
        {
            if (_source[_position] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
            _position++;
        }
    }

    private void SkipComment()
    {
        while (_position < _source.Length && _source[_position] != '\n')
        {
            _position++;
            _column++;
        }
    }

    private Token ReadNumber()
    {
        int startLine = _line;
        int startColumn = _column;
        var value = new StringBuilder();
        bool hasDecimal = false;

        while (_position < _source.Length)
        {
            char current = _source[_position];

            if (char.IsDigit(current))
            {
                value.Append(current);
            }
            else if (current == '.' && !hasDecimal)
            {
                hasDecimal = true;
                value.Append(current);
            }
            else
            {
                break;
            }

            _position++;
            _column++;
        }

        // Inspector-authored numeric literals are formatted invariantly and may use exponent
        // notation (for example 1E-06). Accept it as one number token so the VM and the exposed
        // variable contract agree on every finite double value.
        if (_position < _source.Length && _source[_position] is 'e' or 'E')
        {
            int probe = _position + 1;
            if (probe < _source.Length && _source[probe] is '+' or '-') probe++;
            int digits = probe;
            while (probe < _source.Length && char.IsDigit(_source[probe])) probe++;
            if (probe > digits)
            {
                while (_position < probe)
                {
                    value.Append(_source[_position++]);
                    _column++;
                }
            }
        }

        return new Token(TokenType.Number, value.ToString(), startLine, startColumn);
    }

    private Token ReadString()
    {
        int startLine = _line;
        int startColumn = _column;
        var value = new StringBuilder();

        _position++;
        _column++;

        while (_position < _source.Length && _source[_position] != '"')
        {
            if (_source[_position] == '\\' && _position + 1 < _source.Length)
            {
                _position++;
                _column++;
                char escaped = _source[_position];
                value.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '"' => '"',
                    _ => escaped
                });
            }
            else
            {
                value.Append(_source[_position]);
            }

            _position++;
            _column++;

            if (_source[_position - 1] == '\n')
            {
                _line++;
                _column = 1;
            }
        }

        if (_position >= _source.Length)
        {
            var ex = new Exception($"Unterminated string at line {startLine}");
            ex.Data["Line"] = startLine;
            throw ex;
        }

        _position++;
        _column++;

        return new Token(TokenType.String, value.ToString(), startLine, startColumn);
    }

    private Token ReadIdentifierOrKeyword()
    {
        int startLine = _line;
        int startColumn = _column;
        var value = new StringBuilder();

        while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
        {
            value.Append(_source[_position]);
            _position++;
            _column++;
        }

        string identifier = value.ToString();

        TokenType type = identifier.ToLower() switch
        {
            "if" => TokenType.If,
            "else" => TokenType.Else,
            "while" => TokenType.While,
            "function" => TokenType.Function,
            "return" => TokenType.Return,
            "true" => TokenType.True,
            "false" => TokenType.False,
            "repeat" => TokenType.Repeat,
            "with" => TokenType.With,
            "from" => TokenType.From,
            "for" => TokenType.For,
            "var" => TokenType.Var,
            _ => TokenType.Identifier
        };

        return new Token(type, identifier, startLine, startColumn);
    }

    private Token ReadOperatorOrDelimiter()
    {
        int startLine = _line;
        int startColumn = _column;
        char current = _source[_position];

        _position++;
        _column++;

        if (current == '<' && Peek() == '=')
        {
            _position++;
            _column++;
            return new Token(TokenType.LessThanOrEqual, "<=", startLine, startColumn);
        }
        if (current == '>' && Peek() == '=')
        {
            _position++;
            _column++;
            return new Token(TokenType.GreaterThanOrEqual, ">=", startLine, startColumn);
        }
        if (current == '=' && Peek() == '=')
        {
            _position++;
            _column++;
            return new Token(TokenType.Equal, "==", startLine, startColumn);
        }
        if (current == '!' && Peek() == '=')
        {
            _position++;
            _column++;
            return new Token(TokenType.NotEqual, "!=", startLine, startColumn);
        }
        if (current == '&' && Peek() == '&')
        {
            _position++;
            _column++;
            return new Token(TokenType.And, "&&", startLine, startColumn);
        }
        if (current == '|' && Peek() == '|')
        {
            _position++;
            _column++;
            return new Token(TokenType.Or, "||", startLine, startColumn);
        }
        if (current == '&' && Peek() != '&')
            return new Token(TokenType.Ampersand, "&", startLine, startColumn);
        if (current == '|' && Peek() != '|')
            return new Token(TokenType.Pipe, "|", startLine, startColumn);
        if (current == '^')
            return new Token(TokenType.Caret, "^", startLine, startColumn);
        if (current == '~')
            return new Token(TokenType.Tilde, "~", startLine, startColumn);
        if (current == '<' && Peek() == '<')
        {
            _position++;
            _column++;
            return new Token(TokenType.ShiftLeft, "<<", startLine, startColumn);
        }
        if (current == '>' && Peek() == '>')
        {
            _position++;
            _column++;
            return new Token(TokenType.ShiftRight, ">>", startLine, startColumn);
        }

        return current switch
        {
            '+' => Peek() switch {
                '+' => ConsumeAndReturn(TokenType.PlusPlus, "++"),
                '=' => ConsumeAndReturn(TokenType.PlusEqual, "+="),
                _ => new Token(TokenType.Plus, "+", startLine, startColumn)
            },
            '-' => Peek() switch {
                '-' => ConsumeAndReturn(TokenType.MinusMinus, "--"),
                '=' => ConsumeAndReturn(TokenType.MinusEqual, "-="),
                _ => new Token(TokenType.Minus, "-", startLine, startColumn)
            },
            '*' => Peek() == '=' ? ConsumeAndReturn(TokenType.MultiplyEqual, "*=") : new Token(TokenType.Multiply, "*", startLine, startColumn),
            '/' => Peek() == '=' ? ConsumeAndReturn(TokenType.DivideEqual, "/=") : new Token(TokenType.Divide, "/", startLine, startColumn),
            '%' => new Token(TokenType.Modulo, "%", startLine, startColumn),
            '=' => new Token(TokenType.Assign, "=", startLine, startColumn),
            '(' => new Token(TokenType.LeftParen, "(", startLine, startColumn),
            ')' => new Token(TokenType.RightParen, ")", startLine, startColumn),
            '{' => new Token(TokenType.LeftBrace, "{", startLine, startColumn),
            '}' => new Token(TokenType.RightBrace, "}", startLine, startColumn),
            ',' => new Token(TokenType.Comma, ",", startLine, startColumn),
            ';' => new Token(TokenType.Semicolon, ";", startLine, startColumn),
            '<' => new Token(TokenType.LessThan, "<", startLine, startColumn),
            '>' => new Token(TokenType.GreaterThan, ">", startLine, startColumn),
            '!' => new Token(TokenType.Not, "!", startLine, startColumn),
            '?' => new Token(TokenType.Question, "?", startLine, startColumn),
            ':' => new Token(TokenType.Colon, ":", startLine, startColumn),
            '.' => new Token(TokenType.Dot, ".", startLine, startColumn),
            '[' => new Token(TokenType.LeftBracket, "[", startLine, startColumn),
            ']' => new Token(TokenType.RightBracket, "]", startLine, startColumn),
            _ => new Token(TokenType.Unknown, current.ToString(), startLine, startColumn)
        };
    }

    private Token ConsumeAndReturn(TokenType type, string value)
    {
        int startLine = _line;
        int startColumn = _column;
        _position++;
        _column++;
        return new Token(type, value, startLine, startColumn);
    }

    private char Peek()
    {
        return _position < _source.Length ? _source[_position] : '\0';
    }

    private char PeekNext()
    {
        return _position + 1 < _source.Length ? _source[_position + 1] : '\0';
    }
}
