#nullable disable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    Break,
    Continue,
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

/// <summary>
/// One token of a Script. A value, not an object: a large Script has millions of them (6.1 million
/// in GenesisCraft's 17 MB McBlockModels), all alive until it is compiled, and as objects they were
/// most of the garbage collector's work while a game started.
/// </summary>
public readonly struct Token
{
    public TokenType Type { get; }
    public string Value { get; }
    public int Line { get; }
    public int Column { get; }

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
    // Optimised code at once, as PgslParser's: a game reads its Scripts while it starts.
    private const MethodImplOptions Hot = MethodImplOptions.AggressiveOptimization;

    private readonly string _source;
    private int _position;
    private int _line;
    private int _column;

    // Each name and number is made into a string once: generated data Scripts repeat a few of them
    // millions of times (GenesisCraft's 17 MB McBlockModels is DsListAdd(bmQ, 0.0625); again and
    // again), and a string for each was most of their compile and its garbage.
    private readonly Dictionary<string, string> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _wordLookup;
    private const int LongestSharedWord = 64;

    public PgslLexer(string source)
    {
        _source = source;
        _position = 0;
        _line = 1;
        _column = 1;
        _wordLookup = _words.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    [MethodImpl(Hot)]
    private string Word(int start, int length)
    {
        if (length > LongestSharedWord) return _source.Substring(start, length);
        ReadOnlySpan<char> word = _source.AsSpan(start, length);
        if (_wordLookup.TryGetValue(word, out string known)) return known;
        string made = word.ToString();
        _words.Add(made, made);
        return made;
    }

    [MethodImpl(Hot)]
    public List<Token> Tokenize()
    {
        // Sized once for a large Script, not grown and copied again and again: generated data has
        // a token every three characters or less, other code one every four to six.
        var tokens = new List<Token>(Math.Min(_source.Length * 2 / 5 + 16, 1 << 24));

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

    [MethodImpl(Hot)]
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

    [MethodImpl(Hot)]
    private Token ReadNumber()
    {
        int startLine = _line;
        int startColumn = _column;
        int start = _position;
        bool hasDecimal = false;

        while (_position < _source.Length)
        {
            char current = _source[_position];

            if (char.IsDigit(current))
            {
            }
            else if (current == '.' && !hasDecimal)
            {
                hasDecimal = true;
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
                _column += probe - _position;
                _position = probe;
            }
        }

        return new Token(TokenType.Number, Word(start, _position - start), startLine, startColumn);
    }

    [MethodImpl(Hot)]
    private Token ReadString()
    {
        int startLine = _line;
        int startColumn = _column;

        _position++;
        _column++;

        // Text without escapes is the source as it is between the quotes.
        int end = _source.AsSpan(_position).IndexOfAny('"', '\\');
        if (end >= 0 && _source[_position + end] == '"')
        {
            int first = _position;
            for (; _position < first + end; _position++)
            {
                _column++;
                if (_source[_position] == '\n')
                {
                    _line++;
                    _column = 1;
                }
            }
            _position++;
            _column++;
            return new Token(TokenType.String, Word(first, end), startLine, startColumn);
        }

        var value = new StringBuilder();
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

    [MethodImpl(Hot)]
    private Token ReadIdentifierOrKeyword()
    {
        int startLine = _line;
        int startColumn = _column;
        int start = _position;
        bool plain = true;

        while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
        {
            char c = _source[_position];
            if (c >= 128 || c == 'I') plain = false;
            _position++;
            _column++;
        }

        string identifier = Word(start, _position - start);

        // ASCII without 'I' lower-cases the same in every culture: matched as it is. Anything else
        // is lower-cased by the culture, as it always was.
        TokenType type = plain ? Keyword(identifier) : identifier.ToLower() switch
        {
            "if" => TokenType.If,
            "else" => TokenType.Else,
            "while" => TokenType.While,
            "function" => TokenType.Function,
            "return" => TokenType.Return,
            "break" => TokenType.Break,
            "continue" => TokenType.Continue,
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

    [MethodImpl(Hot)]
    private static TokenType Keyword(string word)
    {
        static bool Is(string word, string keyword) => string.Equals(word, keyword, StringComparison.OrdinalIgnoreCase);
        switch (word.Length)
        {
            case 2: if (Is(word, "if")) return TokenType.If; break;
            case 3: if (Is(word, "for")) return TokenType.For; if (Is(word, "var")) return TokenType.Var; break;
            case 4:
                if (Is(word, "else")) return TokenType.Else;
                if (Is(word, "true")) return TokenType.True;
                if (Is(word, "with")) return TokenType.With;
                if (Is(word, "from")) return TokenType.From;
                break;
            case 5:
                if (Is(word, "while")) return TokenType.While;
                if (Is(word, "break")) return TokenType.Break;
                if (Is(word, "false")) return TokenType.False;
                break;
            case 6: if (Is(word, "return")) return TokenType.Return; if (Is(word, "repeat")) return TokenType.Repeat; break;
            case 8: if (Is(word, "function")) return TokenType.Function; if (Is(word, "continue")) return TokenType.Continue; break;
        }
        return TokenType.Identifier;
    }

    [MethodImpl(Hot)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek()
    {
        return _position < _source.Length ? _source[_position] : '\0';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char PeekNext()
    {
        return _position + 1 < _source.Length ? _source[_position + 1] : '\0';
    }
}
