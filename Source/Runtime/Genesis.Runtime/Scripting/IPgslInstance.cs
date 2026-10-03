namespace Genesis.Shared.Scripting
{
    /// <summary>Game instance exposed to the VM for With-blocks and context switching.</summary>
    public interface IPgslInstance
    {
        void SetActiveContext();

        /// <summary>Called when a with-block has finished with this instance.</summary>
        void EndActiveContext() { }
    }
}
