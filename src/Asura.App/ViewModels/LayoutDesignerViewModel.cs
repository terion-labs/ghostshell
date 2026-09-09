using Asura.Core;
using Asura.Docking;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Inpc.Controls;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns an isolated, transactional layout edit on a live Dock tree. The
/// designer hosts the same Dock model the runtime workspace uses — recursive
/// proportional splits, drag-to-dock, splitter resizes — so what is designed is
/// exactly what opens. Saving serializes the Dock tree as the layout's
/// authoritative geometry and projects it onto the durable slot grid that
/// screens, previews, and the validator consume. Instances are UI-owned and are
/// not thread-safe.
/// </summary>
public sealed class LayoutDesignerViewModel : ObservableObject
{
    public const double DefaultPanelMinimumWidth = 220;
    public const double DefaultPanelMinimumHeight = 140;

    /// <summary>
    /// The designer canvas is a miniature of the screen, so its leaves carry
    /// compact minimums of their own. Enforcing the runtime panel minimums here
    /// would forbid splits the real screen can hold comfortably.
    /// </summary>
    private const double DesignerLeafMinimumWidth = 72;
    private const double DesignerLeafMinimumHeight = 56;

    private readonly LayoutDefinition _original;
    private readonly IDockSerializer _serializer = DockLayoutSerializer.Create();
    private readonly RuntimeDockFactory _factory;
    private readonly Dictionary<string, LayoutDesignerSlotViewModel> _slotsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LayoutMinimumSize> _minimumSizes =
        new(StringComparer.Ordinal);
    private DockWorkspaceManager? _workspaceManager;
    private IRootDock _layout = null!;
    private string _name;
    private string? _selectedSlotId;
    private IReadOnlyList<LayoutDesignerSlotViewModel> _slots = [];
    private DefinitionValidationIssue? _lastOperationIssue;
    private bool _matchesOriginal = true;
    private bool _isProjectionValid = true;
    private bool _isMutatingTopology;

    public LayoutDesignerViewModel(
        LayoutDefinition definition,
        long? expectedRevision)
    {
        _original = definition ?? throw new ArgumentNullException(nameof(definition));
        var validation = LayoutValidator.Validate(definition);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"The source layout is invalid: {FormatIssues(validation.Issues)}",
                nameof(definition));
        }

        ExpectedRevision = expectedRevision;
        _name = definition.Name;
        foreach (var slot in definition.Slots)
        {
            _minimumSizes[slot.Id.Value] = slot.MinimumSize;
        }

        _factory = new RuntimeDockFactory(ResolveContext);
        _factory.LayoutMutated += OnFactoryLayoutMutated;
        LoadLayout(Restore(definition.DockLayoutJson) ?? BuildFromSlots(definition));
        PublishState();
    }

    public static LayoutDesignerViewModel CreateNew(string name = "Untitled layout")
    {
        var definition = new LayoutDefinition(
            LayoutId.New(),
            LayoutDefinition.CurrentSchemaVersion,
            name,
            new LayoutGrid(2, 1),
            [
                new(
                    new LayoutSlotId("slot-1"),
                    new LayoutGridBounds(0, 0, 1, 1),
                    new LayoutMinimumSize(
                        DefaultPanelMinimumWidth,
                        DefaultPanelMinimumHeight)),
                new(
                    new LayoutSlotId("slot-2"),
                    new LayoutGridBounds(1, 0, 1, 1),
                    new LayoutMinimumSize(
                        DefaultPanelMinimumWidth,
                        DefaultPanelMinimumHeight)),
            ]);
        return new(definition, expectedRevision: null);
    }

    public LayoutId Id => _original.Id;

    public int SchemaVersion => _original.SchemaVersion;

    public long? ExpectedRevision { get; }

    public bool IsNew => ExpectedRevision is null;

    /// <summary>The Dock tree the designer canvas renders and mutates directly.</summary>
    public IRootDock DockLayout => _layout;

    public IFactory DockFactory => _factory;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                PublishState();
            }
        }
    }

    /// <summary>Slots are exposed in their traversal order, which is Dock's reading order.</summary>
    public IReadOnlyList<LayoutDesignerSlotViewModel> Slots => _slots;

    public int PanelCount => _slots.Count;

    public string? SelectedSlotId => _selectedSlotId;

    public LayoutDesignerSlotViewModel? SelectedSlot => _slots
        .FirstOrDefault(slot => slot.IsSelected);

    public string GridSummary => PanelCount == 1
        ? "1 panel"
        : $"{PanelCount} panels";

    /// <summary>
    /// Dirty means saving would produce a different definition, not that Dock
    /// raised an event: the first arrange normalizes proportions, which is a
    /// change to the tree but not to what would be saved. Comparing the
    /// projected slot set against the original keeps an untouched saved layout
    /// clean and lets an edit that is later undone read as clean again.
    /// </summary>
    public bool IsDirty => IsNew
        || !_matchesOriginal
        || !StringComparer.Ordinal.Equals(_name, _original.Name);

    public string DirtyStatus => IsNew
        ? "Unsaved new layout"
        : IsDirty
            ? "Unsaved changes"
            : "Saved definition";

    public bool IsValid => _isProjectionValid && !string.IsNullOrWhiteSpace(Name);

    public bool CanSave => IsDirty && IsValid;

    public string ValidationSummary => IsValid
        ? "Layout is valid."
        : string.IsNullOrWhiteSpace(Name)
            ? "A layout name is required."
            : "The layout could not be projected onto a valid grid.";

    public DefinitionValidationIssue? LastOperationIssue => _lastOperationIssue;

    public bool HasOperationError => LastOperationIssue is not null;

    /// <summary>
    /// What the canvas does right now, stated where the user is looking. The
    /// gestures are the runtime workspace's own.
    /// </summary>
    public string GridHint =>
        "Split from a panel's header · drag a divider to resize · drag a panel onto another to rearrange";

    public LayoutDesignerOperationResult SelectSlot(string slotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        if (!_slotsById.ContainsKey(slotId))
        {
            return Reject(
                DefinitionValidationCode.UnknownSlot,
                $"Layout slot '{slotId}' does not exist.",
                slotId);
        }

        _selectedSlotId = slotId;
        if (FindDocument(slotId) is { Owner: IDock owner } document)
        {
            owner.ActiveDockable = document;
        }

        ClearOperationIssue();
        foreach (var slot in _slots)
        {
            slot.IsSelected = StringComparer.Ordinal.Equals(slot.Id, _selectedSlotId);
        }

        OnPropertyChanged(nameof(SelectedSlotId));
        OnPropertyChanged(nameof(SelectedSlot));
        return LayoutDesignerOperationResult.Applied;
    }

    public LayoutDesignerOperationResult SplitSlot(
        string slotId,
        LayoutDesignerSplitDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        var document = FindDocument(slotId);
        if (document?.Owner is not IDock target)
        {
            return Reject(
                DefinitionValidationCode.UnknownSlot,
                $"Layout slot '{slotId}' does not exist.",
                slotId);
        }

        var added = CreateSlotDocument(NextSlotId());
        _factory.SplitToDock(
            target,
            CreateLeaf(added),
            direction == LayoutDesignerSplitDirection.Down
                ? DockOperation.Bottom
                : DockOperation.Right);
        _selectedSlotId = added.Id;
        ClearOperationIssue();
        RefreshFromLayout();
        return LayoutDesignerOperationResult.Applied;
    }

    /// <summary>
    /// Adds a panel without a pointer gesture by halving the largest panel along
    /// its longer side, so "add a panel" is never a dead end.
    /// </summary>
    public LayoutDesignerOperationResult AddSlot()
    {
        var donor = _slots.MaxBy(slot => slot.WidthShare * slot.HeightShare);
        if (donor is null)
        {
            return Reject(
                DefinitionValidationCode.Required,
                "The layout has no panel to split.",
                Id.Value);
        }

        return SplitSlot(
            donor.Id,
            donor.WidthShare >= donor.HeightShare
                ? LayoutDesignerSplitDirection.Right
                : LayoutDesignerSplitDirection.Down);
    }

    public LayoutDesignerOperationResult RemoveSlot(string slotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        if (_slots.Count == 1)
        {
            return Reject(
                DefinitionValidationCode.Required,
                "A layout must contain at least one panel.",
                slotId);
        }

        var document = FindDocument(slotId);
        if (document is null)
        {
            return Reject(
                DefinitionValidationCode.UnknownSlot,
                $"Layout slot '{slotId}' does not exist.",
                slotId);
        }

        _isMutatingTopology = true;
        try
        {
            // Removing the leaf rather than the lone document keeps Dock from
            // leaving an empty "No documents" hole where the panel was.
            if (document.Owner is IDock { VisibleDockables.Count: 1 } leaf
                && leaf.Owner is IDock)
            {
                _factory.RemoveDockable(leaf, collapse: true);
            }
            else
            {
                _factory.RemoveDockable(document, collapse: true);
            }

            DockLayoutTopology.Normalize(_factory, _layout);
        }
        finally
        {
            _isMutatingTopology = false;
        }

        ForgetSlot(slotId);
        ClearOperationIssue();
        RefreshFromLayout();
        return LayoutDesignerOperationResult.Applied;
    }

    public LayoutDesignerOperationResult RemoveSelectedSlot() =>
        _selectedSlotId is { Length: > 0 } selected
            ? RemoveSlot(selected)
            : Reject(
                DefinitionValidationCode.UnknownSlot,
                "Select a panel first.",
                null);

    public void Reset()
    {
        var nameChanged = !StringComparer.Ordinal.Equals(_name, _original.Name);
        _name = _original.Name;
        _selectedSlotId = null;
        _slotsById.Clear();
        _factory.ContextLocator?.Clear();
        _minimumSizes.Clear();
        foreach (var slot in _original.Slots)
        {
            _minimumSizes[slot.Id.Value] = slot.MinimumSize;
        }

        ClearOperationIssue();
        LoadLayout(Restore(_original.DockLayoutJson) ?? BuildFromSlots(_original));
        if (nameChanged)
        {
            OnPropertyChanged(nameof(Name));
        }

        PublishState();
    }

    public LayoutDesignerCancelDisposition RequestCancel() => IsDirty
        ? LayoutDesignerCancelDisposition.ConfirmDiscard
        : LayoutDesignerCancelDisposition.Close;

    public LayoutDesignerSaveRequest CreateSaveRequest()
    {
        var definition = BuildDefinition(includeDockLayout: true);
        var validation = LayoutValidator.Validate(definition);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"The layout cannot be saved: {FormatIssues(validation.Issues)}");
        }

        return new(definition, ExpectedRevision);
    }

    // Layout construction. The designer restores the layout's own Dock geometry
    // when it has one and only falls back to synthesizing a tree from the slot
    // grid for definitions that predate the designer, e.g. imported ones.

    private void LoadLayout(IRootDock layout)
    {
        _layout = layout;
        _factory.InitModel(layout);
        DockLayoutTopology.Normalize(_factory, layout);
        if (_workspaceManager is not null)
        {
            _workspaceManager.WorkspaceDirtyChanged -= OnWorkspaceDirtyChanged;
            _workspaceManager.StopTracking();
        }

        _workspaceManager = new DockWorkspaceManager(_serializer);
        _workspaceManager.WorkspaceDirtyChanged += OnWorkspaceDirtyChanged;
        _workspaceManager.TrackLayout(layout);
        SyncSlots();
        OnPropertyChanged(nameof(DockLayout));
    }

    private IRootDock? Restore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var restored = _serializer.Deserialize<IRootDock>(
                DockLayoutPayloadCodec.Decode(json));
            if (restored is null)
            {
                return null;
            }

            RuntimeDockLayoutController.NormalizeRestoredLayout(restored);
            // The designer edits a single canvas; floating windows belong to
            // live workspaces, not to a saved geometry template.
            restored.Windows?.Clear();
            foreach (var dockable in EnumerateDockables(restored))
            {
                switch (dockable)
                {
                    case IDocument document:
                        AdoptDocument(document);
                        break;
                    case DocumentDock leaf:
                        leaf.EnableWindowDrag = false;
                        break;
                }
            }

            return restored;
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException
                or InvalidDataException
                or NotSupportedException
                or InvalidOperationException)
        {
            return null;
        }
    }

    private IRootDock BuildFromSlots(LayoutDefinition definition)
    {
        var body = BuildRegion(
            [.. definition.Slots],
            left: 0,
            right: definition.Grid.Columns,
            top: 0,
            bottom: definition.Grid.Rows);

        var root = (IRootDock)_factory.CreateRootDock();
        root.Id = "layout-designer-root";
        root.Title = "Layout designer";
        root.IsCollapsable = false;
        root.VisibleDockables = _factory.CreateList(body);
        root.ActiveDockable = body;
        root.Windows = _factory.CreateList<IDockWindow>();
        return root;
    }

    /// <summary>
    /// Builds a Dock subtree for the slots inside one grid region by recursive
    /// guillotine cuts, so a slot spanning several rows or columns keeps its
    /// real shape instead of being flattened into row bands. Horizontal bands
    /// are preferred so traversal order reads top-to-bottom, left-to-right.
    /// </summary>
    private IDockable BuildRegion(
        IReadOnlyList<LayoutSlotDefinition> slots,
        int left,
        int right,
        int top,
        int bottom)
    {
        if (slots.Count == 1)
        {
            return CreateLeaf(CreateSlotDocument(slots[0].Id.Value));
        }

        var bands = SplitAlongAxis(
            slots,
            top,
            bottom,
            slot => slot.Bounds.Row,
            slot => slot.Bounds.Row + slot.Bounds.RowSpan);
        if (bands.Count > 1)
        {
            return CreateProportional(
                Orientation.Vertical,
                [.. bands
                    .Select(band => (
                        Child: BuildRegion(band.Slots, left, right, band.Start, band.End),
                        Proportion: (double)(band.End - band.Start) / (bottom - top)))]);
        }

        var columns = SplitAlongAxis(
            slots,
            left,
            right,
            slot => slot.Bounds.Column,
            slot => slot.Bounds.Column + slot.Bounds.ColumnSpan);
        if (columns.Count > 1)
        {
            return CreateProportional(
                Orientation.Horizontal,
                [.. columns
                    .Select(column => (
                        Child: BuildRegion(column.Slots, column.Start, column.End, top, bottom),
                        Proportion: (double)(column.End - column.Start) / (right - left)))]);
        }

        // No clean cut on either axis: a pinwheel arrangement, which Dock's
        // strictly recursive splits cannot express. Approximate with row bands
        // so every slot stays present and editable.
        var rows = slots
            .GroupBy(slot => slot.Bounds.Row)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var rowSlots = group.OrderBy(slot => slot.Bounds.Column).ToArray();
                var row = rowSlots.Length == 1
                    ? CreateLeaf(CreateSlotDocument(rowSlots[0].Id.Value))
                    : (IDockable)CreateProportional(
                        Orientation.Horizontal,
                        [.. rowSlots
                            .Select(slot => (
                                Child: (IDockable)CreateLeaf(CreateSlotDocument(slot.Id.Value)),
                                Proportion: 1d / rowSlots.Length))]);
                return row;
            })
            .ToArray();
        return rows.Length == 1
            ? rows[0]
            : CreateProportional(
                Orientation.Vertical,
                [.. rows.Select(row => (Child: row, Proportion: 1d / rows.Length))]);
    }

    /// <summary>
    /// Partitions a region's slots at every axis position no slot spans across.
    /// Bands that contain no slot are dropped; the layout contract does not
    /// require full coverage, and Dock has nothing to show for an empty band.
    /// </summary>
    private static List<(int Start, int End, IReadOnlyList<LayoutSlotDefinition> Slots)>
        SplitAlongAxis(
            IReadOnlyList<LayoutSlotDefinition> slots,
            int start,
            int end,
            Func<LayoutSlotDefinition, int> startOf,
            Func<LayoutSlotDefinition, int> endOf)
    {
        var cuts = new List<int> { start };
        for (var cut = start + 1; cut < end; cut++)
        {
            if (slots.All(slot => endOf(slot) <= cut || startOf(slot) >= cut))
            {
                cuts.Add(cut);
            }
        }

        cuts.Add(end);
        var groups = new List<(int Start, int End, IReadOnlyList<LayoutSlotDefinition> Slots)>();
        for (var index = 0; index < cuts.Count - 1; index++)
        {
            var bandStart = cuts[index];
            var bandEnd = cuts[index + 1];
            var bandSlots = slots
                .Where(slot => startOf(slot) >= bandStart && endOf(slot) <= bandEnd)
                .ToArray();
            if (bandSlots.Length > 0)
            {
                groups.Add((bandStart, bandEnd, bandSlots));
            }
        }

        return groups;
    }

    private ProportionalDock CreateProportional(
        Orientation orientation,
        IReadOnlyList<(IDockable Child, double Proportion)> children)
    {
        var visible = new List<IDockable>((children.Count * 2) - 1);
        for (var index = 0; index < children.Count; index++)
        {
            if (index > 0)
            {
                visible.Add(new ProportionalDockSplitter
                {
                    CanResize = true,
                    ResizePreview = false,
                });
            }

            children[index].Child.Proportion = children[index].Proportion;
            visible.Add(children[index].Child);
        }

        return new ProportionalDock
        {
            Orientation = orientation,
            IsCollapsable = false,
            VisibleDockables = _factory.CreateList(visible.ToArray()),
            ActiveDockable = children[0].Child,
        };
    }

    private Document CreateSlotDocument(string id)
    {
        var document = new Document { Id = id };
        AdoptDocument(document);
        return document;
    }

    private void AdoptDocument(IDocument document)
    {
        var id = string.IsNullOrWhiteSpace(document.Id) ? NextSlotId() : document.Id;
        document.Id = id;
        document.Title = "Panel";
        document.Context = TrackSlot(id);
        document.CanClose = false;
        document.CanFloat = false;
        document.CanDrag = true;
        document.CanDrop = true;
        document.MinWidth = DesignerLeafMinimumWidth;
        document.MinHeight = DesignerLeafMinimumHeight;
    }

    private LayoutDesignerSlotViewModel TrackSlot(string id)
    {
        if (!_slotsById.TryGetValue(id, out var slot))
        {
            slot = new LayoutDesignerSlotViewModel(id);
            _slotsById[id] = slot;
        }

        RegisterContextLocator(id);
        return slot;
    }

    private void RegisterContextLocator(string id)
    {
        if (_factory.ContextLocator is { } locator)
        {
            locator[id] = () => ResolveContext(id);
        }
    }

    private void ForgetSlot(string id)
    {
        _slotsById.Remove(id);
        _factory.ContextLocator?.Remove(id);
    }

    private DocumentDock CreateLeaf(IDocument document) => new()
    {
        Id = $"slot-dock-{document.Id}",
        Title = document.Title,
        IsCollapsable = true,
        CanCloseLastDockable = true,
        CanCreateDocument = false,
        EnableWindowDrag = false,
        ActiveDockable = document,
        VisibleDockables = _factory.CreateList<IDockable>(document),
    };

    private object? ResolveContext(string id) =>
        _slotsById.TryGetValue(id, out var slot) ? slot : null;

    // State projection. Slot identities persist across mutations; only their
    // order, canvas share, and selection are republished.

    private void SyncSlots()
    {
        var regions = DockLayoutProjection.CollectRegions(_layout);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<LayoutDesignerSlotViewModel>(regions.Count);
        var order = 1;
        foreach (var region in regions)
        {
            // The document's Context is the authoritative slot instance. Factory
            // mutation events fire while a split is still rearranging the tree,
            // so a sync pass can observe a document as momentarily absent and
            // drop its tracking; re-registering from the document keeps the
            // canvas and the sidebar on one instance instead of letting a later
            // pass mint a second one the canvas never sees.
            if (region.Document.Context is not LayoutDesignerSlotViewModel slot)
            {
                AdoptDocument(region.Document);
                slot = (LayoutDesignerSlotViewModel)region.Document.Context!;
            }

            _slotsById[slot.Id] = slot;
            RegisterContextLocator(slot.Id);
            seen.Add(slot.Id);
            slot.Order = order++;
            slot.WidthShare = region.Width;
            slot.HeightShare = region.Height;
            ordered.Add(slot);
        }

        foreach (var stale in _slotsById.Keys.Except(seen, StringComparer.Ordinal).ToArray())
        {
            ForgetSlot(stale);
        }

        if (_selectedSlotId is null || !seen.Contains(_selectedSlotId))
        {
            _selectedSlotId = ordered.FirstOrDefault()?.Id;
        }

        foreach (var slot in ordered)
        {
            slot.IsSelected = StringComparer.Ordinal.Equals(slot.Id, _selectedSlotId);
        }

        _slots = ordered;
    }

    private void RefreshFromLayout()
    {
        SyncSlots();
        PublishState();
    }

    private void OnFactoryLayoutMutated(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (!_isMutatingTopology)
        {
            RefreshFromLayout();
        }
    }

    private void OnWorkspaceDirtyChanged(
        object? sender,
        DockWorkspaceDirtyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (!eventArgs.IsDirty)
        {
            return;
        }

        _workspaceManager?.MarkClean();
        if (!_isMutatingTopology)
        {
            RefreshFromLayout();
        }
    }

    private void PublishState()
    {
        _isProjectionValid = ComputeProjectionValidity();
        _matchesOriginal = ComputeMatchesOriginal();
        OnPropertyChanged(nameof(Slots));
        OnPropertyChanged(nameof(PanelCount));
        OnPropertyChanged(nameof(SelectedSlotId));
        OnPropertyChanged(nameof(SelectedSlot));
        OnPropertyChanged(nameof(GridSummary));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DirtyStatus));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ValidationSummary));
    }

    /// <summary>
    /// Whether saving now would reproduce the original geometry. Comparison is
    /// fractional, not cell-for-cell: the projection normalizes granularity, so
    /// a 12 × 8 grid of half-width slots and a 2 × 1 grid of them are the same
    /// layout. Slot order is compared as a set for the same reason — the
    /// designer derives order from reading order.
    /// </summary>
    private bool ComputeMatchesOriginal()
    {
        var (grid, slots) = DockLayoutProjection.ProjectSlots(_layout, MinimumSizeFor);
        if (slots.Count != _original.Slots.Count)
        {
            return false;
        }

        var originalById = _original.Slots.ToDictionary(
            slot => slot.Id.Value,
            StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (!originalById.TryGetValue(slot.Id.Value, out var original)
                || slot.MinimumSize != original.MinimumSize
                || !FractionsMatch(slot.Bounds, grid, original.Bounds, _original.Grid))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Half of the projection's snapping step: geometry that agrees to within
    /// rounding is the same geometry, while a real one-track resize is not.
    /// </summary>
    private const double FractionTolerance = 0.021;

    private static bool FractionsMatch(
        LayoutGridBounds first,
        LayoutGrid firstGrid,
        LayoutGridBounds second,
        LayoutGrid secondGrid) =>
        FractionNear(first.Column, firstGrid.Columns, second.Column, secondGrid.Columns)
        && FractionNear(first.ColumnSpan, firstGrid.Columns, second.ColumnSpan, secondGrid.Columns)
        && FractionNear(first.Row, firstGrid.Rows, second.Row, secondGrid.Rows)
        && FractionNear(first.RowSpan, firstGrid.Rows, second.RowSpan, secondGrid.Rows);

    private static bool FractionNear(
        int firstValue,
        int firstTotal,
        int secondValue,
        int secondTotal) =>
        Math.Abs(
            (firstValue / (double)Math.Max(1, firstTotal))
            - (secondValue / (double)Math.Max(1, secondTotal)))
        <= FractionTolerance;

    private bool ComputeProjectionValidity()
    {
        try
        {
            return LayoutValidator
                .Validate(BuildDefinition(includeDockLayout: false))
                .Issues
                .All(issue => issue.Code == DefinitionValidationCode.Required
                    && string.IsNullOrWhiteSpace(Name));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private LayoutDefinition BuildDefinition(bool includeDockLayout)
    {
        var (grid, slots) = DockLayoutProjection.ProjectSlots(_layout, MinimumSizeFor);
        return new LayoutDefinition(
            Id,
            SchemaVersion,
            Name,
            grid,
            slots,
            includeDockLayout
                ? DockLayoutPayloadCodec.Encode(_serializer.Serialize<IRootDock>(_layout))
                : null);
    }

    private LayoutMinimumSize MinimumSizeFor(string slotId) =>
        _minimumSizes.TryGetValue(slotId, out var minimum)
            ? minimum
            : new LayoutMinimumSize(
                DefaultPanelMinimumWidth,
                DefaultPanelMinimumHeight);

    private IDocument? FindDocument(string id) =>
        EnumerateDockables(_layout)
            .OfType<IDocument>()
            .FirstOrDefault(document =>
                StringComparer.Ordinal.Equals(document.Id, id));

    private string NextSlotId()
    {
        var used = EnumerateDockables(_layout)
            .OfType<IDocument>()
            .Select(document => document.Id)
            .Concat(_slotsById.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var suffix = used.Count + 1;
        while (used.Contains($"slot-{suffix}"))
        {
            suffix++;
        }

        return $"slot-{suffix}";
    }

    private static IEnumerable<IDockable> EnumerateDockables(IDockable root)
    {
        var pending = new Stack<IDockable>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is not IDock { VisibleDockables: { } children })
            {
                continue;
            }

            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(children[index]);
            }
        }
    }

    private void ClearOperationIssue()
    {
        if (_lastOperationIssue is null)
        {
            return;
        }

        _lastOperationIssue = null;
        OnPropertyChanged(nameof(LastOperationIssue));
        OnPropertyChanged(nameof(HasOperationError));
    }

    private LayoutDesignerOperationResult Reject(
        DefinitionValidationCode code,
        string message,
        string? target)
    {
        var issue = new DefinitionValidationIssue(code, message, target);
        _lastOperationIssue = issue;
        OnPropertyChanged(nameof(LastOperationIssue));
        OnPropertyChanged(nameof(HasOperationError));
        return LayoutDesignerOperationResult.Rejected(issue);
    }

    private static string FormatIssues(IEnumerable<DefinitionValidationIssue> issues) =>
        string.Join(" ", issues.Select(issue => issue.Message));
}
