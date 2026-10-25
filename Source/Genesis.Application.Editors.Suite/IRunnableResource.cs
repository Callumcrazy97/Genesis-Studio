namespace Genesis.Application.Editors.Suite;

/// <summary>
/// An editor that can run the resource it has open as that resource's own sandbox: an Object's
/// live sandbox, a sound playing, a model's clip, a terrain's preview. Studio's Run This Resource
/// (Shift+F5) calls it for the selected or open resource; a Room runs as the game instead.
/// </summary>
public interface IRunnableResource
{
    /// <summary>Runs the resource; false when there is nothing to run.</summary>
    bool RunResource();
}
