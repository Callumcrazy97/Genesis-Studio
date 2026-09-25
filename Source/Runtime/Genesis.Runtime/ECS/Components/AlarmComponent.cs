using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public struct AlarmComponent : IComponent
    {
        // 12 countdown timers; decremented each step.
        // Fire the AlarmN event when a value reaches 0; -1 = inactive.
        public int Alarm0,  Alarm1,  Alarm2,  Alarm3;
        public int Alarm4,  Alarm5,  Alarm6,  Alarm7;
        public int Alarm8,  Alarm9,  Alarm10, Alarm11;
    }
}
