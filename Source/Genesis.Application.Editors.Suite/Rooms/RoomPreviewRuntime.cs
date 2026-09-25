using System.Numerics;
using System.Windows.Forms;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Controls in-editor Play / Pause / Stop simulation without permanently modifying editor data.
/// </summary>
public sealed class RoomPreviewRuntime : IDisposable
{
    private readonly RoomEditorControl _editor;
    private readonly System.Windows.Forms.Timer _timer;
    private RoomAsset? _snapshot;
    private float _savedCameraX;
    private float _savedCameraY;
    private float _savedZoom;
    private bool _isSimulating;
    private bool _isPaused;

    public event Action? SimulationStateChanged;

    public RoomPreviewRuntime(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _timer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
        _timer.Tick += OnSimulationTick;
    }

    public bool IsSimulating => _isSimulating;

    public bool IsPaused => _isPaused;

    public void Play()
    {
        if (_isSimulating)
        {
            if (_isPaused)
            {
                _isPaused = false;
                _timer.Start();
                SimulationStateChanged?.Invoke();
            }
            return;
        }

        // Take snapshot of current room state so playing does NOT permanently modify editor data
        _snapshot = _editor.Room.DeepClone();
        _savedCameraX = _editor.Viewport.Camera2DX;
        _savedCameraY = _editor.Viewport.Camera2DY;
        _savedZoom = _editor.Viewport.Zoom2D;

        _isSimulating = true;
        _isPaused = false;
        _timer.Start();
        SimulationStateChanged?.Invoke();
    }

    public void Pause()
    {
        if (!_isSimulating) return;
        _isPaused = !_isPaused;
        if (_isPaused)
        {
            _timer.Stop();
        }
        else
        {
            _timer.Start();
        }
        SimulationStateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!_isSimulating) return;
        _timer.Stop();
        _isSimulating = false;
        _isPaused = false;

        // Restore pre-play room state
        if (_snapshot is not null)
        {
            _editor.RestoreRoomSnapshot(_snapshot);
            _snapshot = null;
        }

        _editor.Viewport.Camera2DX = _savedCameraX;
        _editor.Viewport.Camera2DY = _savedCameraY;
        _editor.Viewport.Zoom2D = _savedZoom;

        _editor.Viewport.Host.Invalidate();
        SimulationStateChanged?.Invoke();
    }

    private void OnSimulationTick(object? sender, EventArgs e)
    {
        if (!_isSimulating || _isPaused) return;

        // Advance room time for background scrolling and UV animators
        _editor.AdvanceRoomTime(1f / 60f);

        // Track follow target in runtime viewports if enabled
        RoomAsset room = _editor.Room;
        if (room.UsesViewports)
        {
            RoomViewport? vp = room.Viewports.Find(v => v.Enabled && !string.IsNullOrWhiteSpace(v.FollowTarget));
            if (vp is not null)
            {
                RoomNode? target = room.Nodes.Find(n => string.Equals(n.Name, vp.FollowTarget, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                {
                    float targetX = target.Transform.X;
                    float targetY = target.Transform.Y;
                    if (vp.FollowSpeedX < 0) vp.SourceX = targetX - vp.SourceWidth * 0.5f;
                    else vp.SourceX += (targetX - (vp.SourceX + vp.SourceWidth * 0.5f)) * 0.1f;

                    if (vp.FollowSpeedY < 0) vp.SourceY = targetY - vp.SourceHeight * 0.5f;
                    else vp.SourceY += (targetY - (vp.SourceY + vp.SourceHeight * 0.5f)) * 0.1f;
                }
            }
        }

        _editor.Viewport.Host.Invalidate();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
