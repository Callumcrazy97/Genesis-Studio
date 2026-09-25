using Genesis.Runtime.Particles;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private ListView? _eventList;
    private ComboBox? _eventSource;
    private ComboBox? _eventTarget;
    private ComboBox? _eventTrigger;
    private NumericUpDown? _eventProbability;
    private NumericUpDown? _eventCount;
    private NumericUpDown? _eventVelocity;
    private Button? _removeEventButton;
    private bool _syncingEventEditor;
    private int _selectedEventIndex = -1;

    private void BuildParticleEventControls()
    {
        FlowLayoutPanel page = AddInspectorPage("Events");
        AddInfoCard(page, "Sub-emitter events",
            "Spawn one emitter from another emitter's Birth, Death or Collision event. Links use stable emitter identity, so rename and reorder are safe.");

        _eventList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Height = 150,
            Width = 254,
        };
        _eventList.Columns.Add("Source", 74);
        _eventList.Columns.Add("Event", 66);
        _eventList.Columns.Add("Target", 74);
        _eventList.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingEventEditor) return;
            _selectedEventIndex = _eventList.SelectedIndices.Count > 0 ? _eventList.SelectedIndices[0] : -1;
            SyncSelectedEventFields();
        };
        page.Controls.Add(_eventList);

        FlowLayoutPanel buttons = new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 8),
        };
        Button add = new() { Text = "Add link", Width = 116, Height = 28 };
        _removeEventButton = new Button { Text = "Remove", Width = 116, Height = 28 };
        EditorChrome.StyleField(add);
        EditorChrome.StyleField(_removeEventButton);
        add.Click += (_, _) => AddParticleEventLink();
        _removeEventButton.Click += (_, _) => RemoveParticleEventLink();
        buttons.Controls.Add(add);
        buttons.Controls.Add(_removeEventButton);
        page.Controls.Add(buttons);

        _eventSource = EventCombo();
        _eventTarget = EventCombo();
        _eventTrigger = EventCombo();
        foreach (ParticleEventTrigger trigger in Enum.GetValues<ParticleEventTrigger>())
            if (trigger != ParticleEventTrigger.None)
                _eventTrigger.Items.Add(trigger);

        _eventProbability = EventNumber(0, 1, 0.05m, 2);
        _eventCount = EventNumber(1, 32, 1, 0);
        _eventVelocity = EventNumber(0, 4, 0.05m, 2);

        AddInspectorRow(page, "Source", _eventSource);
        AddInspectorRow(page, "Event", _eventTrigger);
        AddInspectorRow(page, "Target", _eventTarget);
        AddInspectorRow(page, "Probability", _eventProbability);
        AddInspectorRow(page, "Spawn count", _eventCount);
        AddInspectorRow(page, "Inherit velocity", _eventVelocity);

        _eventSource.SelectedIndexChanged += (_, _) => ApplySelectedEventFields();
        _eventTarget.SelectedIndexChanged += (_, _) => ApplySelectedEventFields();
        _eventTrigger.SelectedIndexChanged += (_, _) => ApplySelectedEventFields();
        _eventProbability.ValueChanged += (_, _) => ApplySelectedEventFields();
        _eventCount.ValueChanged += (_, _) => ApplySelectedEventFields();
        _eventVelocity.ValueChanged += (_, _) => ApplySelectedEventFields();

        RefreshParticleEventEditor();
    }

    private static ComboBox EventCombo()
    {
        ComboBox combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        EditorChrome.StyleField(combo);
        return combo;
    }

    private static NumericUpDown EventNumber(decimal minimum, decimal maximum, decimal increment, int decimals)
    {
        NumericUpDown number = new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Increment = increment,
            DecimalPlaces = decimals,
            Width = 140,
        };
        EditorChrome.StyleField(number);
        return number;
    }

    private void AddParticleEventLink()
    {
        if (!PrepareParticleOperation()) return;
        List<(string Id, string Name)> emitters = EventEmitterChoices();
        if (emitters.Count < 2)
        {
            ShowParticleNotice("Add at least two emitters before creating a sub-emitter event link.");
            return;
        }

        string source = ParticleEffectEditing.Id(_effect, Math.Clamp(_selectedEmitterIndex, 0, emitters.Count - 1));
        string target = emitters.First(item => !string.Equals(item.Id, source, StringComparison.OrdinalIgnoreCase)).Id;
        _effect.EventLinks ??= [];
        _effect.EventLinks.Add(new ParticleEventLink
        {
            SourceEmitterId = source,
            TargetEmitterId = target,
            Trigger = ParticleEventTrigger.Death,
            Probability = 1,
            Count = 1,
        });
        _selectedEventIndex = _effect.EventLinks.Count - 1;
        CommitParticleEdit("Add particle event link");
        RefreshParticleEventEditor();
        _viewport.Invalidate(true);
    }

    private void RemoveParticleEventLink()
    {
        if (!PrepareParticleOperation() || _effect.EventLinks is null
            || _selectedEventIndex < 0 || _selectedEventIndex >= _effect.EventLinks.Count)
            return;

        _effect.EventLinks.RemoveAt(_selectedEventIndex);
        _selectedEventIndex = Math.Min(_selectedEventIndex, _effect.EventLinks.Count - 1);
        CommitParticleEdit("Remove particle event link");
        RefreshParticleEventEditor();
        _viewport.Invalidate(true);
    }

    private void ApplySelectedEventFields()
    {
        if (_syncingEventEditor || _effect.EventLinks is null
            || _selectedEventIndex < 0 || _selectedEventIndex >= _effect.EventLinks.Count
            || _eventSource?.SelectedItem is not string sourceName
            || _eventTarget?.SelectedItem is not string targetName
            || _eventTrigger?.SelectedItem is not ParticleEventTrigger trigger
            || _eventProbability is null || _eventCount is null || _eventVelocity is null)
            return;

        string sourceId = EventEmitterId(sourceName);
        string targetId = EventEmitterId(targetName);
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId)) return;

        ParticleEventLink link = _effect.EventLinks[_selectedEventIndex];
        if (string.Equals(link.SourceEmitterId, sourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(link.TargetEmitterId, targetId, StringComparison.OrdinalIgnoreCase)
            && link.Trigger == trigger
            && Math.Abs(link.Probability - (double)_eventProbability.Value) < 1e-9
            && link.Count == (int)_eventCount.Value
            && Math.Abs(link.InheritVelocity - (double)_eventVelocity.Value) < 1e-9)
            return;

        link.SourceEmitterId = sourceId;
        link.TargetEmitterId = targetId;
        link.Trigger = trigger;
        link.Probability = (double)_eventProbability.Value;
        link.Count = (int)_eventCount.Value;
        link.InheritVelocity = (double)_eventVelocity.Value;
        CommitParticleEdit("Edit particle event link");
        RefreshParticleEventListOnly();
        _viewport.Invalidate(true);
    }

    private void RefreshParticleEventEditor()
    {
        if (_eventList is null || _eventSource is null || _eventTarget is null) return;
        _effect.EventLinks ??= [];
        PruneParticleEventLinks();

        _syncingEventEditor = true;
        try
        {
            List<(string Id, string Name)> choices = EventEmitterChoices();
            string? source = _eventSource.SelectedItem as string;
            string? target = _eventTarget.SelectedItem as string;
            _eventSource.Items.Clear();
            _eventTarget.Items.Clear();
            foreach ((string _, string name) in choices)
            {
                _eventSource.Items.Add(name);
                _eventTarget.Items.Add(name);
            }
            if (source is not null && _eventSource.Items.Contains(source)) _eventSource.SelectedItem = source;
            if (target is not null && _eventTarget.Items.Contains(target)) _eventTarget.SelectedItem = target;

            RefreshParticleEventListOnly();
            if (_effect.EventLinks.Count == 0) _selectedEventIndex = -1;
            else _selectedEventIndex = Math.Clamp(_selectedEventIndex < 0 ? 0 : _selectedEventIndex, 0, _effect.EventLinks.Count - 1);

            if (_selectedEventIndex >= 0 && _selectedEventIndex < _eventList.Items.Count)
                _eventList.Items[_selectedEventIndex].Selected = true;
            SyncSelectedEventFields();
        }
        finally
        {
            _syncingEventEditor = false;
        }
    }

    private void RefreshParticleEventListOnly()
    {
        if (_eventList is null) return;
        bool previous = _syncingEventEditor;
        _syncingEventEditor = true;
        try
        {
            _eventList.BeginUpdate();
            _eventList.Items.Clear();
            foreach (ParticleEventLink link in _effect.EventLinks ?? [])
            {
                ListViewItem item = new(EventEmitterName(link.SourceEmitterId));
                item.SubItems.Add(link.Trigger.ToString());
                item.SubItems.Add(EventEmitterName(link.TargetEmitterId));
                item.ToolTipText = $"{link.Probability:P0} · count {link.Count} · velocity ×{link.InheritVelocity:0.##}";
                _eventList.Items.Add(item);
            }
            _eventList.EndUpdate();
        }
        finally { _syncingEventEditor = previous; }
    }

    private void SyncSelectedEventFields()
    {
        if (_eventSource is null || _eventTarget is null || _eventTrigger is null
            || _eventProbability is null || _eventCount is null || _eventVelocity is null
            || _removeEventButton is null)
            return;

        bool valid = _effect.EventLinks is not null
            && _selectedEventIndex >= 0 && _selectedEventIndex < _effect.EventLinks.Count;
        _removeEventButton.Enabled = valid;
        _eventSource.Enabled = valid;
        _eventTarget.Enabled = valid;
        _eventTrigger.Enabled = valid;
        _eventProbability.Enabled = valid;
        _eventCount.Enabled = valid;
        _eventVelocity.Enabled = valid;
        if (!valid) return;

        bool previous = _syncingEventEditor;
        _syncingEventEditor = true;
        try
        {
            ParticleEventLink link = _effect.EventLinks![_selectedEventIndex];
            _eventSource.SelectedItem = EventEmitterName(link.SourceEmitterId);
            _eventTarget.SelectedItem = EventEmitterName(link.TargetEmitterId);
            _eventTrigger.SelectedItem = link.Trigger;
            _eventProbability.Value = Math.Clamp((decimal)link.Probability, _eventProbability.Minimum, _eventProbability.Maximum);
            _eventCount.Value = Math.Clamp(link.Count, (int)_eventCount.Minimum, (int)_eventCount.Maximum);
            _eventVelocity.Value = Math.Clamp((decimal)link.InheritVelocity, _eventVelocity.Minimum, _eventVelocity.Maximum);
        }
        finally { _syncingEventEditor = previous; }
    }

    private void PruneParticleEventLinks()
    {
        if (_effect.EventLinks is null || _effect.EventLinks.Count == 0) return;
        HashSet<string> ids = EventEmitterChoices()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _effect.EventLinks.RemoveAll(link => link is null
            || !ids.Contains(link.SourceEmitterId)
            || !ids.Contains(link.TargetEmitterId)
            || link.Trigger == ParticleEventTrigger.None
            || link.Count <= 0);
    }

    private List<(string Id, string Name)> EventEmitterChoices()
    {
        List<(string Id, string Name)> result = [];
        int count = ParticleEffectEditing.Count(_effect);
        for (int i = 0; i < count; i++)
            result.Add((ParticleEffectEditing.Id(_effect, i), ParticleEffectEditing.Name(_effect, i)));
        return result;
    }

    private string EventEmitterName(string id)
    {
        foreach ((string emitterId, string name) in EventEmitterChoices())
            if (string.Equals(emitterId, id, StringComparison.OrdinalIgnoreCase))
                return name;
        return "(missing)";
    }

    private string EventEmitterId(string name)
    {
        foreach ((string id, string emitterName) in EventEmitterChoices())
            if (string.Equals(emitterName, name, StringComparison.Ordinal))
                return id;
        return string.Empty;
    }
}
