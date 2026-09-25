using System;
using System.Collections.Generic;

namespace Genesis.Runtime.Scripting
{
    public enum DebugChannel { Runtime, Editor }

    public static class DebugLog
    {
        private sealed class ScopeFrame
        {
            public DebugChannel Channel;
            public string EditorId;
        }

        private static readonly Stack<ScopeFrame> _stack = new Stack<ScopeFrame>();

        public static DebugChannel CurrentChannel =>
            _stack.Count > 0 ? _stack.Peek().Channel : DebugChannel.Runtime;

        public static string CurrentEditorId =>
            _stack.Count > 0 ? _stack.Peek().EditorId : null;

        public static IDisposable PushEditor(string editorId)
        {
            _stack.Push(new ScopeFrame { Channel = DebugChannel.Editor, EditorId = editorId });
            return new PopDisposable();
        }

        public static void Pop()
        {
            if (_stack.Count > 0) _stack.Pop();
        }

        public static bool ShouldLog1D =>
            ScriptingDebugSettings.DebuggingEnabled &&
            (CurrentChannel == DebugChannel.Runtime
                ? ScriptingDebugSettings.DebugRuntime1DPgslVm
                : ScriptingDebugSettings.DebugEditor1DPgslVm);

        public static bool ShouldLog1DShadow =>
            ScriptingDebugSettings.DebuggingEnabled &&
            (CurrentChannel == DebugChannel.Runtime
                ? ScriptingDebugSettings.DebugRuntime1DShadowTrace
                : ScriptingDebugSettings.DebugEditor1DShadowTrace);

        public static bool ShouldLog2D =>
            ScriptingDebugSettings.DebuggingEnabled &&
            (CurrentChannel == DebugChannel.Runtime
                ? ScriptingDebugSettings.DebugRuntime2DDraw
                : ScriptingDebugSettings.DebugEditor2DDraw);

        public static bool ShouldLog3DDraw =>
            ScriptingDebugSettings.DebuggingEnabled &&
            (CurrentChannel == DebugChannel.Runtime
                ? ScriptingDebugSettings.DebugRuntime3DDraw
                : ScriptingDebugSettings.DebugEditor3DDraw);

        public static bool ShouldLog3DPipeline =>
            ScriptingDebugSettings.DebuggingEnabled &&
            (CurrentChannel == DebugChannel.Runtime
                ? ScriptingDebugSettings.DebugRuntime3DPipeline
                : ScriptingDebugSettings.DebugEditor3DPipeline);

        public static string FormatPrefix(string dimensionTag)
        {
            if (CurrentChannel == DebugChannel.Runtime)
                return $"[Runtime][{dimensionTag}]";
            string id = CurrentEditorId;
            return !string.IsNullOrEmpty(id)
                ? $"[Editor][{dimensionTag}][{id}]"
                : $"[Editor][{dimensionTag}]";
        }

        private sealed class PopDisposable : IDisposable
        {
            public void Dispose() => Pop();
        }
    }
}
