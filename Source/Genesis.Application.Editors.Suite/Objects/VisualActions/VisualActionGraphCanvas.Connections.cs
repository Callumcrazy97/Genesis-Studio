using System.Drawing.Drawing2D;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

public sealed partial class VisualActionGraphCanvas
{
    public sealed record Connection(string Source, string Target, string Parameter, PointF From, PointF To, Color Colour);
    private readonly List<Connection> _connections = [];
    private Connection? _selectedConnection;
    private Connection? _reconnecting;
    private (string Id, string Name)? _wireInput;
    private (string Id, string Name, RectangleF Bounds)? _pendingField;
    private Point _connectionDown;
    public IReadOnlyList<Connection> Connections => _connections;
    public event Action<Connection>? ConnectionDeleteRequested;

    public bool CancelConnection()
    {
        if (_wireSource is null && _wireInput is null && _pendingField is null && _reconnecting is null) return false;
        _wireSource = null; _wireInput = null; _pendingField = null; _reconnecting = null;
        Capture = false; Invalidate(); return true;
    }

    private void DrawDataConnections(Graphics graphics)
    {
        foreach (var block in _blocks)
        foreach (var parameter in block.Parameters)
        {
            var producer = _blocks.FirstOrDefault(candidate => candidate.ResultVariable.Length > 0 && candidate.ResultVariable == parameter.Value);
            if (producer is not null && _outputPins.TryGetValue(producer.Id + ":data", out var from) && _inputPins.TryGetValue((block.Id, parameter.Name), out var to))
                DrawConnection(graphics, new(producer.Id, block.Id, parameter.Name, from, to, BlueprintActions.Colour(BlueprintActions.OutputType(producer))));
            else if (_routineEntry
                     && _routineParameters.FirstOrDefault(candidate => candidate.Name == parameter.Value) is var routineParameter
                     && !string.IsNullOrEmpty(routineParameter.Name)
                     && _outputPins.TryGetValue("$param:" + routineParameter.Name + ":data", out from)
                     && _inputPins.TryGetValue((block.Id, parameter.Name), out to))
                DrawConnection(graphics, new("$param:" + routineParameter.Name, block.Id, parameter.Name, from, to, BlueprintActions.Colour(routineParameter.Type)));
        }
        if (_wireSource is not null && _outputPins.TryGetValue(_wireSource, out var pin)) DrawWire(graphics, pin, _pointerGraph, _executionWire ? Color.White : Color.DeepSkyBlue);
        DrawPendingConnection(graphics);
    }

    private void DrawStructuredConnection(Graphics graphics, RectangleF previous, RectangleF bounds, VisualActionBlock block, string entry)
    {
        if (IsPureData(block)) return;
        var upstreamId = _nodeBounds.FirstOrDefault(pair => pair.Value == previous).Key;
        var upstream = _blocks.FirstOrDefault(candidate => candidate.Id == upstreamId);
        if (block.DetachedChain != (upstream?.DetachedChain ?? "")) return;
        string id = upstreamId ?? entry;
        PointF pin = upstreamId is not null ? new(previous.Right, previous.Y + 48) : new(previous.Right, previous.Y + previous.Height / 2);
        _outputPins[id] = pin;
        if (upstreamId is null) DrawExecutionPin(graphics, pin);
        DrawConnection(graphics, new(id, block.Id, "$exec", pin, new(bounds.X, bounds.Y + 48), block.DetachedChain.Length > 0 ? Color.Gray : Color.WhiteSmoke));
    }

    private static bool SameConnection(Connection? left, Connection right) => left is not null
        && left.Source == right.Source && left.Target == right.Target && left.Parameter == right.Parameter;

    private static GraphicsPath ConnectionPath(PointF from, PointF to)
    {
        float bend = Math.Max(45, Math.Abs(to.X - from.X) * .5f);
        var path = new GraphicsPath();
        path.AddBezier(from, new(from.X + bend, from.Y), new(to.X - bend, to.Y), to);
        return path;
    }

    private void DrawConnection(Graphics graphics, Connection connection)
    {
        _connections.Add(connection);
        using var path = ConnectionPath(connection.From, connection.To);
        using var pen = new Pen(SameConnection(_selectedConnection, connection) ? Color.Gold : connection.Colour,
            SameConnection(_selectedConnection, connection) ? 3.5f : 2f);
        graphics.DrawPath(pen, path);
        if (!SameConnection(_selectedConnection, connection)) return;
        using var brush = new SolidBrush(Color.Gold);
        foreach (var point in new[] { connection.From, connection.To }) graphics.FillEllipse(brush, point.X - 5, point.Y - 5, 10, 10);
    }

    public bool DeleteSelectedConnection()
    {
        if (_selectedConnection is not { } connection) return false;
        _selectedConnection = null; ConnectionDeleteRequested?.Invoke(connection); Invalidate(); return true;
    }

    private bool BeginConnectionInput(MouseEventArgs e, PointF point)
    {
        if (e.Button != MouseButtons.Left) return false;
        _connectionDown = e.Location;
        foreach (var (id, pin) in _outputPins)
            if (Near(pin, point))
            {
                _selectedConnection = null; _wireSource = id; _executionWire = !id.EndsWith(":data", StringComparison.Ordinal);
                Capture = true; return true;
            }
        foreach (var (key, pin) in _inputPins)
            if (Near(pin, point))
            {
                _selectedConnection = _connections.FirstOrDefault(connection => connection.Target == key.Id && connection.Parameter == key.Name);
                _wireInput = key; _executionWire = key.Name == "$exec";
                Capture = true; return true;
            }
        foreach (var (key, bounds) in _fieldHits)
            if (bounds.Contains(point))
            {
                _selectedConnection = null; SelectBlock(key.Id); _pendingField = (key.Id, key.Name, bounds);
                Capture = true; return true;
            }
        foreach (var connection in _connections.AsEnumerable().Reverse())
        {
            using var path = ConnectionPath(connection.From, connection.To); using var hitPen = new Pen(Color.White, 12f / _zoom);
            if (!path.IsOutlineVisible(point, hitPen)) continue;
            SelectBlock(null); _selectedConnection = connection; _reconnecting = connection;
            Capture = true; Invalidate(); return true;
        }
        _selectedConnection = null;
        return false;
    }

    private void MoveConnectionInput(MouseEventArgs e)
    {
        if (Distance(_connectionDown, e.Location) < 6) return;
        if (_pendingField is { } field && !field.Name.StartsWith('$'))
        {
            _wireInput = (field.Id, field.Name); _executionWire = false; _pendingField = null;
        }
        if (_reconnecting is { } connection)
        {
            var start = GraphPoint(_connectionDown);
            _executionWire = connection.Parameter == "$exec";
            if (SquaredDistance(start, connection.From) < SquaredDistance(start, connection.To))
                _wireInput = (connection.Target, connection.Parameter);
            else _wireSource = connection.Source + (_executionWire ? "" : ":data");
            _reconnecting = null;
        }
    }

    private bool EndConnectionInput(MouseEventArgs e)
    {
        var point = GraphPoint(e.Location);
        if (_pendingField is { } field)
        {
            _pendingField = null; Capture = false; BeginFieldEdit(field.Id, field.Name, field.Bounds); return true;
        }
        if (_wireInput is { } input)
        {
            string? output = _outputPins.FirstOrDefault(pair => Near(pair.Value, point, 13)
                && pair.Key.EndsWith(":data", StringComparison.Ordinal) != _executionWire).Key;
            if (output is null && !_executionWire)
            {
                var block = _blocks.FirstOrDefault(block => block.ResultVariable.Length > 0 && _nodeBounds.GetValueOrDefault(block.Id).Contains(point));
                if (block is not null) output = block.Id + ":data";
            }
            _wireInput = null; Capture = false;
            if (output is null && !_executionWire && _selectedConnection is { } old)
            {
                var newInput = _inputPins.FirstOrDefault(pair => pair.Key.Name != "$exec" && Near(pair.Value, point, 13)).Key;
                if (newInput.Id is null) newInput = _fieldHits.FirstOrDefault(pair => !pair.Key.Name.StartsWith('$') && pair.Value.Contains(point)).Key;
                if (newInput.Id is not null && (old.Target != newInput.Id || old.Parameter != newInput.Name))
                    DataConnectionMoved?.Invoke(old, old.Source, newInput.Id, newInput.Name);
            }
            if (output is not null)
            {
                if (_executionWire) ExecutionConnectionRequested?.Invoke(output, input.Id);
                else DataConnectionRequested?.Invoke(output[..^5], input.Id, input.Name);
            }
            _selectedConnection = null; Invalidate(); return true;
        }
        if (_wireSource is { } source)
        {
            var target = _inputPins.FirstOrDefault(pair => Near(pair.Value, point, 13) && (pair.Key.Name == "$exec") == _executionWire).Key;
            if (target.Id is null && !_executionWire)
                target = _fieldHits.FirstOrDefault(pair => !pair.Key.Name.StartsWith('$') && pair.Value.Contains(point)).Key;
            _wireSource = null; Capture = false;
            if (target.Id is not null)
            {
                // Moving a connected endpoint is one builder operation, so an invalid drop leaves
                // the old wire intact and an accepted drop can undo the entire reconnection.
                if (_selectedConnection is { Parameter: not "$exec" } old && !_executionWire && (old.Target != target.Id || old.Parameter != target.Name))
                    DataConnectionMoved?.Invoke(old, source[..^5], target.Id, target.Name);
                else if (_executionWire) ExecutionConnectionRequested?.Invoke(source, target.Id);
                else DataConnectionRequested?.Invoke(source[..^5], target.Id, target.Name);
            }
            _selectedConnection = null; Invalidate(); return true;
        }
        if (_reconnecting is not null) { _reconnecting = null; Capture = false; return true; }
        return false;
    }

    public event Action<Connection, string, string, string>? DataConnectionMoved;
    private void DrawPendingConnection(Graphics graphics)
    {
        if (_wireInput is { } input && _inputPins.TryGetValue(input, out var pin))
            DrawWire(graphics, _pointerGraph, pin, _executionWire ? Color.White : Color.DeepSkyBlue);
    }
    private static float SquaredDistance(PointF a, PointF b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
    private static bool Near(PointF a, PointF b, float radius = 10) => SquaredDistance(a, b) < radius * radius;
}
