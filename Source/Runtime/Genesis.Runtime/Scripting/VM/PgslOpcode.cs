#nullable disable
namespace Genesis.Runtime.Scripting.VM;

public enum Opcode
{
    NOP,
    PUSH_NULL,
    PUSH,
    POP,
    DUP,
    SWAP,
    LOAD_CONST,
    LOAD_VAR,
    STORE_VAR,
    ADD,
    SUB,
    MUL,
    DIV,
    MOD,
    NEG,
    AND,
    OR,
    NOT,
    EQ,
    NEQ,
    LT,
    LTE,
    GT,
    GTE,
    JUMP,
    JUMP_IF_FALSE,
    JUMP_IF_TRUE,
    CALL,
    RETURN,
    PRINT,
    GET_INDEX,
    SET_INDEX,
    WITH_START,
    WITH_NEXT,
    WITH_END,
    BIT_AND,
    BIT_OR,
    BIT_XOR,
    BIT_NOT,
    SHL,
    SHR,
    // Fusion register-slot opcodes — bypass string-keyed variable lookup
    LOAD_REG,   // operand: int slot (0–63 = instance register, backed by PgslContext)
    STORE_REG,  // operand: int slot
    LOAD_LOCAL, // operand: int slot (64–127, frame-local; falls through to LOAD_VAR if frame slots not yet allocated)
    STORE_LOCAL,// operand: int slot
    CALL_NATIVE // operand: (int id, int argCount) — skips bridge dictionary lookup
}

public class Instruction
{
    public Opcode Opcode { get; set; }
    public object Operand { get; set; }
    public int LineNumber { get; set; }

    public Instruction(Opcode opcode, object operand = null, int lineNumber = 0)
    {
        Opcode = opcode;
        Operand = operand;
        LineNumber = lineNumber;
    }

    public override string ToString()
    {
        if (Operand == null)
            return Opcode.ToString();
        return $"{Opcode} {Operand}";
    }
}
