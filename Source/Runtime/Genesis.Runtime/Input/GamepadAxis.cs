namespace Genesis.Runtime.Input
{
    /// <summary>
    /// A whole stick axis or a trigger, as an input action's binding that reads as a value: a stick's
    /// axis from -1 to 1 (right and up are positive, on both sticks), a trigger from 0 to 1.
    /// </summary>
    public enum GamepadAxis
    {
        LeftStickX,
        LeftStickY,
        RightStickX,
        RightStickY,
        LeftTrigger,
        RightTrigger,
    }
}
