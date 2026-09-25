namespace Genesis.Shared.Scripting
{
    /// <summary>Game instance exposed to the VM for With-blocks and context switching.</summary>
    public interface IPgslInstance
    {
        void SetActiveContext();
    }
}
