using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using GhostShell.Application;

namespace GhostShell.App.ViewModels;

public enum DatabaseWorkspaceMode
{
    Data,
    Structure,
    Indexes,
}

public enum DatabaseOverviewMode
{
    Objects,
    ErDiagram,
}

public sealed class DatabaseDriverOptionViewModel(DatabaseDriverDescriptor descriptor)
{
    public string Id { get; } = descriptor.Id;

    public string DisplayName { get; } = descriptor.DisplayName;

    public string ConnectionStringHint { get; } = descriptor.ConnectionStringHint;

    public bool IsFileBased { get; } = descriptor.IsFileBased;

    public bool CanListDatabases { get; } = descriptor.CanListDatabases;
}

public sealed class DatabaseTableItemViewModel(DatabaseTableDescriptor table) : ObservableObject
{
    private bool _isSelected;

    public DatabaseTableDescriptor Descriptor { get; } = table;

    public string Name { get; } = table.DisplayName;

    public bool IsView { get; } = table.Kind == DatabaseTableKind.View;

    public string KindLabel { get; } = table.Kind == DatabaseTableKind.View ? "View" : "Table";

    /// <summary>The sidebar highlights the object the workspace is showing.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// One editable detached cell. Parsing happens here once, at the user-input
/// boundary, so the database layer receives typed values rather than UI text.
/// </summary>
public sealed class DatabaseResultCellViewModel : ObservableObject
{
    internal const int MaximumDisplayCharacters = 4_096;

    private const int BinaryPreviewByteCount = 32;

    private DatabaseEditValueState _originalState;
    private object? _originalValue;
    private string _originalEditText;
    private string _originalDisplayText;
    private readonly bool _canAssignValue;
    private readonly bool _canUseDefault;
    private DatabaseEditValueState _state;
    private object? _currentValue;
    private string _editText;
    private string _displayText;
    private bool? _booleanValue;
    private string? _validationError;

    public DatabaseResultCellViewModel(
        DatabaseValue value,
        DatabaseColumnDescriptor column,
        double width,
        bool canEdit,
        DatabaseEditValueState? initialState = null)
    {
        Column = column;
        Width = width;
        _originalState = initialState ?? (value.IsNull
            ? DatabaseEditValueState.Null
            : DatabaseEditValueState.Value);
        _canUseDefault = initialState is not null
            && (column.IsReadOnly
                || column.IsIdentity
                || column.DefaultExpression is not null
                || column.IsNullable == true);
        _state = _originalState;
        _originalValue = value.RawValue;
        _currentValue = value.RawValue;
        _editText = value.IsNull || value.RawValue is DatabaseValueContent ? string.Empty : value.ToInvariantText();
        _originalEditText = _editText;
        _displayText = BoundDisplay(_state == DatabaseEditValueState.Value && value.RawValue is null
            ? _editText
            : value.DisplayText);
        _originalDisplayText = _displayText;
        _booleanValue = value.RawValue as bool?;
        _canAssignValue = canEdit && !column.IsReadOnly && !column.IsIdentity;
        IsEditable = _canAssignValue
            && column.ValueKind is not (DatabaseValueKind.Other
                or DatabaseValueKind.Binary
                or DatabaseValueKind.Collection
                or DatabaseValueKind.Json
                or DatabaseValueKind.Network);
        // Values loaded from the provider are already typed. Re-parsing their
        // display text here changes Int32 to Int64 (and similar provider CLR
        // types), which makes untouched rows appear dirty immediately.
        ValidateCurrentValue(parseText: initialState is not null && value.RawValue is null);
    }

    public DatabaseColumnDescriptor Column { get; }

    public double Width { get; }

    public bool IsEditable { get; }

    /// <summary>Whether clipboard text can enter this cell through semantic parsing.</summary>
    public bool CanSetText => IsEditable;

    public bool NeedsFullTextForEditing => CanSetText && _currentValue is DatabaseValueContent;

    internal async Task LoadFullTextForEditingAsync(long availableMemoryBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NeedsFullTextForEditing || _currentValue is not DatabaseValueContent content)
        {
            return;
        }

        const long reserve = 64L * 1024 * 1024;
        // A deliberate editor action may allocate a complete string/document.
        // Budget their copies against available process memory, not a fixed
        // read-only cell-size cap. Full streamed export remains available.
        if (content.Length > int.MaxValue || content.Length > Math.Max(0, availableMemoryBytes - reserve) / 6)
        {
            throw new IOException("There is not enough memory to open this complete value in the text editor. Export the full value to a file, or free memory and retry.");
        }

        await using var source = content.OpenRead();
        var complete = await Task.Run(async () =>
        {
            using var reader = new StreamReader(source, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            // Numeric spill retains its provider CLR identity, including the
            // original key/concurrency value. Display text is not that identity.
            object value = content.ScalarType switch
            {
                null => text,
                DatabaseArrayScalarType.Int128 => Int128.Parse(text, CultureInfo.InvariantCulture),
                DatabaseArrayScalarType.UInt128 => UInt128.Parse(text, CultureInfo.InvariantCulture),
                DatabaseArrayScalarType.BigInteger => BigInteger.Parse(text, CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException("Unsupported detached numeric type."),
            };
            return (Text: text, Value: value);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_currentValue, content))
        {
            return;
        }

        _currentValue = complete.Value;
        _editText = complete.Text;
        if (ReferenceEquals(_originalValue, content))
        {
            _originalValue = complete.Value;
            _originalEditText = complete.Text;
        }
        RaiseValueStateChanged();
        OnPropertyChanged(nameof(NeedsFullTextForEditing));
    }

    public bool CanSetEmpty => IsEditable && Column.ValueKind == DatabaseValueKind.Text;

    public bool CanSetNull => _canAssignValue && Column.IsNullable != false;

    public bool CanSetDefault => _canAssignValue && _canUseDefault;

    /// <summary>
    /// Binary columns stay read-only in the text grid, but a detached byte array
    /// can still be assigned safely by the context menu's file picker.
    /// </summary>
    public bool CanSetBinary => _canAssignValue && Column.ValueKind == DatabaseValueKind.Binary;

    public bool UsesBooleanEditor => IsEditable && Column.ValueKind == DatabaseValueKind.Boolean;

    public bool UsesTextEditor => IsEditable && !UsesBooleanEditor;

    /// <summary>
    /// Kinds whose values outgrow one line — prose and documents, not numbers
    /// or dates. Editing one opens the expanded editor beside the cell.
    /// </summary>
    public bool UsesLargeTextEditor => UsesTextEditor
        && Column.ValueKind is DatabaseValueKind.Text or DatabaseValueKind.Json;

    public bool IsReadOnly => !IsEditable;

    public DatabaseEditValueState State => _state;

    public bool IsNull => _state == DatabaseEditValueState.Null;

    public bool IsDefault => _state == DatabaseEditValueState.Default;

    public object? RawValue => _state == DatabaseEditValueState.Value
        ? _currentValue
        : null;

    public string Text => _state switch
    {
        DatabaseEditValueState.Null => "NULL",
        DatabaseEditValueState.Default => "DEFAULT",
        _ => _displayText,
    };

    /// <summary>
    /// Formats an already materialized value. Detached content requires the
    /// streaming export path or explicit editor loading; this synchronous
    /// property must not substitute a content handle's diagnostic description.
    /// </summary>
    public string FullText => _state switch
    {
        DatabaseEditValueState.Null => "NULL",
        DatabaseEditValueState.Default => "DEFAULT",
        _ when _currentValue is DatabaseValueContent => throw new InvalidOperationException(
            "Detached database values require streaming export or explicit editor loading."),
        _ when Column.ValueKind == DatabaseValueKind.Other => _displayText,
        _ when _currentValue is not null => FormatFullValue(_currentValue),
        _ => _editText,
    };

    public string EditText
    {
        get => _editText;
        set
        {
            if (!UsesTextEditor)
            {
                return;
            }

            var normalized = value ?? string.Empty;
            var textChanged = _currentValue is DatabaseValueContent
                || !string.Equals(_editText, normalized, StringComparison.Ordinal);
            var stateChanged = _state != DatabaseEditValueState.Value;
            if (!textChanged && !stateChanged)
            {
                return;
            }

            _editText = normalized;
            _state = DatabaseEditValueState.Value;
            ParseCurrentText();
            RaiseValueStateChanged();
        }
    }

    public bool? BooleanValue
    {
        get => _booleanValue;
        set
        {
            if (!UsesBooleanEditor)
            {
                return;
            }

            var nextState = value is null
                ? DatabaseEditValueState.Null
                : DatabaseEditValueState.Value;
            if (Equals(_booleanValue, value) && _state == nextState)
            {
                return;
            }

            _state = nextState;
            _currentValue = value;
            _booleanValue = value;
            _editText = value is null ? string.Empty : FormatValue(value.Value);
            _displayText = BoundDisplay(value is null ? "NULL" : _editText);
            ValidationError = value is null && Column.IsNullable == false
                ? $"{Column.Name} does not allow NULL."
                : null;
            RaiseValueStateChanged();
        }
    }

    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    public bool IsValid => ValidationError is null;

    public bool IsDirty => _state != _originalState
        || (_originalValue is DatabaseValueContent && !ReferenceEquals(_currentValue, _originalValue))
        || (_state == DatabaseEditValueState.Value && UsesTextEditor
            ? !string.Equals(_editText, _originalEditText, StringComparison.Ordinal)
            : !ValuesEqual(_currentValue, _originalValue));

    public void SetText(string value)
    {
        if (!CanSetText)
        {
            return;
        }

        if (UsesTextEditor)
        {
            EditText = value;
            return;
        }

        var normalized = value ?? string.Empty;
        _state = DatabaseEditValueState.Value;
        _editText = normalized;
        if (TryParse(normalized, Column.ValueKind, out var parsed, out var error))
        {
            _currentValue = parsed;
            _booleanValue = parsed as bool?;
            _displayText = BoundDisplay(parsed is null ? normalized : FormatValue(parsed));
            ValidationError = null;
        }
        else
        {
            _currentValue = null;
            _booleanValue = null;
            _displayText = BoundDisplay(normalized);
            ValidationError = error;
        }

        RaiseValueStateChanged();
    }

    public void SetEmpty()
    {
        if (CanSetEmpty)
        {
            EditText = string.Empty;
        }
    }

    public void SetNull()
    {
        if (!CanSetNull)
        {
            return;
        }

        _state = DatabaseEditValueState.Null;
        _currentValue = null;
        _booleanValue = null;
        ValidationError = Column.IsNullable == false
            ? $"{Column.Name} does not allow NULL."
            : null;
        RaiseValueStateChanged();
    }

    public void SetDefault()
    {
        if (!CanSetDefault)
        {
            return;
        }

        _state = DatabaseEditValueState.Default;
        _currentValue = null;
        ValidateCurrentValue();
        RaiseValueStateChanged();
    }

    public void SetBinary(ReadOnlyMemory<byte> value)
    {
        if (!CanSetBinary)
        {
            return;
        }

        var bytes = value.ToArray();
        _state = DatabaseEditValueState.Value;
        _currentValue = bytes;
        _editText = Convert.ToHexString(bytes);
        _displayText = BoundDisplay(FormatValue(bytes));
        ValidationError = null;
        RaiseValueStateChanged();
    }

    public void Reset()
    {
        _state = _originalState;
        _currentValue = _originalValue;
        _editText = _originalEditText;
        _displayText = _originalDisplayText;
        _booleanValue = _originalValue as bool?;
        ValidateCurrentValue(parseText: false);
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(BooleanValue));
        RaiseValueStateChanged();
    }

    /// <summary>
    /// Promotes the current value to the concurrency snapshot after the database
    /// accepts a mutation. This must happen before refreshing because a failed
    /// refresh must not leave the same mutation eligible to run again.
    /// </summary>
    public void AcceptChanges()
    {
        var wasDirty = IsDirty;
        _originalState = _state;
        _originalValue = _currentValue;
        _originalEditText = _editText;
        _originalDisplayText = _displayText;
        if (wasDirty)
        {
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public bool TryBuildEdit(out DatabaseColumnEdit edit)
    {
        if (_state == DatabaseEditValueState.Default)
        {
            ValidateCurrentValue();
            edit = new DatabaseColumnEdit(Column.Name, DatabaseEditValueState.Default);
            return IsValid;
        }

        if (_state == DatabaseEditValueState.Null)
        {
            ValidateCurrentValue();
            edit = new DatabaseColumnEdit(Column.Name, DatabaseEditValueState.Null);
            return IsValid;
        }

        // Text setters already parse and validate. Re-parsing an unchanged
        // provider value here can narrow its CLR type or consume a spill handle.
        edit = new DatabaseColumnEdit(Column.Name, DatabaseEditValueState.Value, _currentValue);
        return IsValid;
    }

    public DatabaseColumnEdit BuildOriginalEdit() => _originalState switch
    {
        DatabaseEditValueState.Null => new DatabaseColumnEdit(
            Column.Name,
            DatabaseEditValueState.Null),
        _ => new DatabaseColumnEdit(
            Column.Name,
            DatabaseEditValueState.Value,
            _originalValue),
    };

    private void ParseCurrentText()
    {
        if (TryParse(_editText, Column.ValueKind, out var value, out var error))
        {
            _currentValue = value;
            _displayText = BoundDisplay(value is null ? _editText : FormatValue(value));
            ValidationError = null;
            return;
        }

        _currentValue = null;
        _displayText = BoundDisplay(_editText);
        ValidationError = error;
    }

    private void ValidateCurrentValue(bool parseText = true)
    {
        if (_state == DatabaseEditValueState.Null)
        {
            ValidationError = Column.IsNullable == false
                ? $"{Column.Name} does not allow NULL."
                : null;
            return;
        }

        if (_state == DatabaseEditValueState.Default)
        {
            ValidationError = _canUseDefault
                ? null
                : $"{Column.Name} requires a value.";
            return;
        }

        if (UsesBooleanEditor && _currentValue is null)
        {
            ValidationError = $"{Column.Name} requires true or false.";
            return;
        }

        if (CanSetBinary && _currentValue is null)
        {
            ValidationError = $"{Column.Name} requires a file value.";
            return;
        }

        if (!IsEditable && _currentValue is null)
        {
            ValidationError = $"{Column.Name} requires a value this viewer cannot edit safely.";
            return;
        }

        if (parseText && UsesTextEditor)
        {
            ParseCurrentText();
        }
        else
        {
            ValidationError = null;
        }
    }

    private void RaiseValueStateChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsNull));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(FullText));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(BooleanValue));
        OnPropertyChanged(nameof(RawValue));
        OnPropertyChanged(nameof(IsDirty));
    }

    internal DatabaseResultCellViewModel CopyForNewRow()
    {
        var canCopyValue = _canAssignValue
            && Column.DefaultExpression is null
            && Column.ValueKind != DatabaseValueKind.Other;
        if (!canCopyValue)
        {
            var state = InitialStateForNewRow(Column);
            return new DatabaseResultCellViewModel(
                new DatabaseValue(null, Column.ValueKind, StateText(state)),
                Column,
                Width,
                canEdit: _canAssignValue,
                initialState: state);
        }

        var copiedValue = _currentValue switch
        {
            byte[] bytes => bytes.ToArray(),
            JsonElement element => element.Clone(),
            Array values => values.Clone(),
            _ => _currentValue,
        };
        return new DatabaseResultCellViewModel(
            new DatabaseValue(copiedValue, Column.ValueKind, Text),
            Column,
            Width,
            canEdit: true,
            initialState: _state);
    }

    internal static DatabaseEditValueState InitialStateForNewRow(
        DatabaseColumnDescriptor column)
    {
        if (column.IsReadOnly
            || column.IsIdentity
            || column.DefaultExpression is not null)
        {
            return DatabaseEditValueState.Default;
        }

        return column.IsNullable == true
            ? DatabaseEditValueState.Null
            : DatabaseEditValueState.Value;
    }

    internal static bool TryParse(
        string text,
        DatabaseValueKind kind,
        out object? value,
        out string? error)
    {
        var culture = CultureInfo.InvariantCulture;
        var success = true;
        value = kind switch
        {
            DatabaseValueKind.Text or DatabaseValueKind.Network => text,
            DatabaseValueKind.Json when IsValidJson(text) => text,
            DatabaseValueKind.Boolean when bool.TryParse(text, out var parsed) => parsed,
            DatabaseValueKind.SignedInteger when TryParseSignedInteger(text, culture, out var parsed) => parsed,
            DatabaseValueKind.UnsignedInteger when TryParseUnsignedInteger(text, culture, out var parsed) => parsed,
            DatabaseValueKind.Decimal when decimal.TryParse(text, NumberStyles.Number, culture, out var parsed) => parsed,
            DatabaseValueKind.FloatingPoint when double.TryParse(text, NumberStyles.Float, culture, out var parsed) => parsed,
            DatabaseValueKind.Date when DateOnly.TryParse(text, culture, DateTimeStyles.None, out var parsed) => parsed,
            DatabaseValueKind.Time when TimeOnly.TryParse(text, culture, DateTimeStyles.None, out var parsed) => parsed,
            DatabaseValueKind.Timestamp when DateTime.TryParse(text, culture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            DatabaseValueKind.TimestampWithZone when DateTimeOffset.TryParse(text, culture, DateTimeStyles.RoundtripKind, out var parsed) => parsed,
            DatabaseValueKind.Duration when TimeSpan.TryParse(text, culture, out var parsed) => parsed,
            DatabaseValueKind.Guid when Guid.TryParse(text, out var parsed) => parsed,
            _ => Failed(),
        };
        error = success
            ? null
            : $"{DescribeInvalidValue(text)} is not a valid {kind.ToString().ToLowerInvariant()} value.";
        return success;

        object? Failed()
        {
            success = false;
            return null;
        }
    }

    private static string DescribeInvalidValue(string text)
    {
        const int maximumShownCharacters = 128;
        var oneLine = text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');
        return oneLine.Length <= maximumShownCharacters
            ? oneLine
            : oneLine[..(maximumShownCharacters - 1)] + "…";
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind != JsonValueKind.Undefined;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseSignedInteger(
        string text,
        CultureInfo culture,
        out object value)
    {
        if (long.TryParse(text, NumberStyles.Integer, culture, out var integer))
        {
            value = integer;
            return true;
        }

        if (Int128.TryParse(text, NumberStyles.Integer, culture, out var wideInteger))
        {
            value = wideInteger;
            return true;
        }

        if (BigInteger.TryParse(text, NumberStyles.Integer, culture, out var arbitraryInteger))
        {
            value = arbitraryInteger;
            return true;
        }

        value = 0L;
        return false;
    }

    private static bool TryParseUnsignedInteger(
        string text,
        CultureInfo culture,
        out object value)
    {
        if (ulong.TryParse(text, NumberStyles.Integer, culture, out var integer))
        {
            value = integer;
            return true;
        }

        if (UInt128.TryParse(text, NumberStyles.Integer, culture, out var wideInteger))
        {
            value = wideInteger;
            return true;
        }

        if (BigInteger.TryParse(text, NumberStyles.Integer, culture, out var arbitraryInteger)
            && arbitraryInteger.Sign >= 0)
        {
            value = arbitraryInteger;
            return true;
        }

        value = 0UL;
        return false;
    }

    private static string FormatValue(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        byte[] bytes => FormatBinary(bytes),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatFullValue(object value) => value switch
    {
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        JsonElement element => element.GetRawText(),
        Array values => $"[{string.Join(", ", values.Cast<object?>().Select(FormatCollectionValue))}]",
        _ => FormatValue(value),
    };

    private static string FormatCollectionValue(object? value) => value is null
        ? "NULL"
        : FormatFullValue(value);

    private static string BoundDisplay(string value)
    {
        if (value.Length <= MaximumDisplayCharacters)
        {
            return value;
        }

        var prefixLength = MaximumDisplayCharacters - 1;
        if (char.IsHighSurrogate(value[prefixLength - 1])
            && char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }

        return value[..prefixLength] + "…";
    }

    private static string FormatBinary(byte[] bytes)
    {
        var shown = bytes.AsSpan(0, Math.Min(bytes.Length, BinaryPreviewByteCount));
        var text = $"0x{Convert.ToHexString(shown)}";
        return bytes.Length <= BinaryPreviewByteCount
            ? text
            : $"{text}… ({bytes.Length} bytes)";
    }

    private static string StateText(DatabaseEditValueState state) => state switch
    {
        DatabaseEditValueState.Default => "DEFAULT",
        DatabaseEditValueState.Null => "NULL",
        _ => string.Empty,
    };

    private static bool ValuesEqual(object? left, object? right) =>
        left is byte[] leftBytes && right is byte[] rightBytes
            ? leftBytes.AsSpan().SequenceEqual(rightBytes)
            : Equals(left, right);
}

public sealed class DatabaseResultRowViewModel : ObservableObject
{
    private bool _acceptingChanges;
    private bool _isNew;
    private bool _isSelected;

    public DatabaseResultRowViewModel(
        int number,
        IReadOnlyList<string?> cells,
        IReadOnlyList<double> columnWidths)
        : this(
            number,
            [.. cells.Select(value => DatabaseValue.FromDisplay(value))],
            [.. cells.Select((_, index) => new DatabaseColumnDescriptor(
                $"Column{index + 1}",
                "TEXT",
                DatabaseValueKind.Text))],
            columnWidths,
            canEdit: false)
    {
    }

    public DatabaseResultRowViewModel(
        int number,
        IReadOnlyList<DatabaseValue> values,
        IReadOnlyList<DatabaseColumnDescriptor> columns,
        IReadOnlyList<double> columnWidths,
        bool canEdit,
        bool isNew = false)
    {
        Number = number;
        _isNew = isNew;
        Cells = [.. values
            .Select((value, index) =>
            {
                var column = columns[index];
                DatabaseEditValueState? initialState = isNew
                    ? DatabaseResultCellViewModel.InitialStateForNewRow(column)
                    : null;
                return new DatabaseResultCellViewModel(
                    value,
                    column,
                    index < columnWidths.Count ? columnWidths[index] : 164,
                    canEdit,
                    initialState);
            })];
        foreach (var cell in Cells)
        {
            cell.PropertyChanged += OnCellPropertyChanged;
        }
    }

    private DatabaseResultRowViewModel(
        int number,
        IReadOnlyList<DatabaseResultCellViewModel> cells)
    {
        Number = number;
        _isNew = true;
        Cells = cells;
        foreach (var cell in Cells)
        {
            cell.PropertyChanged += OnCellPropertyChanged;
        }
    }

    public event EventHandler? DirtyStateChanged;

    public int Number { get; }

    public bool IsEven => Number % 2 == 0;

    public bool IsNew => _isNew;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public IReadOnlyList<DatabaseResultCellViewModel> Cells { get; }

    public bool IsDirty => IsNew || Cells.Any(cell => cell.IsDirty);

    public bool IsValid => Cells.All(cell => cell.IsValid);

    /// <summary>
    /// Creates an insert candidate from the row as currently edited. Values
    /// owned by the server or unsafe to round-trip fall back to normal new-row
    /// state; detached binary values are copied so the two rows never alias.
    /// </summary>
    public DatabaseResultRowViewModel DuplicateAsNew(int number) => new(
        number,
        [.. Cells.Select(cell => cell.CopyForNewRow())]);

    public DatabaseInsertedRow BuildInsert() => new(BuildEdits(
        Cells.Where(cell => !cell.Column.IsReadOnly)));

    public DatabaseUpdatedRow BuildUpdate() => new(
        [.. Cells.Where(cell => cell.Column.IsKey).Select(cell => cell.BuildOriginalEdit())],
        BuildEdits(Cells.Where(cell => cell.IsDirty && !cell.Column.IsReadOnly)),
        [.. Cells.Where(cell => !cell.Column.IsKey && IsSafeConcurrencyValue(cell.Column.ValueKind)).Select(cell => cell.BuildOriginalEdit())]);

    public DatabaseDeletedRow BuildDelete() => new(
        [.. Cells.Where(cell => cell.Column.IsKey).Select(cell => cell.BuildOriginalEdit())],
        [.. Cells.Where(cell => !cell.Column.IsKey && IsSafeConcurrencyValue(cell.Column.ValueKind)).Select(cell => cell.BuildOriginalEdit())]);

    public void Reset()
    {
        foreach (var cell in Cells)
        {
            cell.Reset();
        }
    }

    /// <summary>Marks this row and every cell as the last committed snapshot.</summary>
    public void AcceptChanges()
    {
        var wasDirty = IsDirty;
        _acceptingChanges = true;
        try
        {
            foreach (var cell in Cells)
            {
                cell.AcceptChanges();
            }
        }
        finally
        {
            _acceptingChanges = false;
        }

        if (_isNew)
        {
            _isNew = false;
            OnPropertyChanged(nameof(IsNew));
        }

        if (wasDirty)
        {
            OnPropertyChanged(nameof(IsDirty));
            DirtyStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (_acceptingChanges)
        {
            return;
        }

        if (e.PropertyName is nameof(DatabaseResultCellViewModel.IsDirty)
            or nameof(DatabaseResultCellViewModel.IsValid))
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(IsValid));
            DirtyStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsSafeConcurrencyValue(DatabaseValueKind kind) => kind is not
        (DatabaseValueKind.Other
            or DatabaseValueKind.Binary
            or DatabaseValueKind.Collection
            or DatabaseValueKind.Json
            or DatabaseValueKind.Network);

    private static IReadOnlyList<DatabaseColumnEdit> BuildEdits(
        IEnumerable<DatabaseResultCellViewModel> cells)
    {
        var edits = new List<DatabaseColumnEdit>();
        foreach (var cell in cells)
        {
            if (!cell.TryBuildEdit(out var edit))
            {
                throw new InvalidOperationException(
                    cell.ValidationError ?? $"{cell.Column.Name} is invalid.");
            }

            edits.Add(edit);
        }

        return edits;
    }
}

/// <summary>
/// One field in the row inspector — a live window onto the row's cell, not a
/// snapshot of it. Editing works on a draft: Apply stages the draft into the
/// cell exactly like typing in the grid would, Revert discards it. The
/// Save/Revert pair on the toolbar remains what commits or abandons the row.
/// </summary>
public sealed class DatabaseRowFieldViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseResultCellViewModel _cell;
    private string _draft = string.Empty;
    private bool _isEditing;

    internal DatabaseResultCellViewModel Cell => _cell;

    public DatabaseRowFieldViewModel(
        DatabaseResultColumnViewModel column,
        DatabaseResultCellViewModel cell)
    {
        ArgumentNullException.ThrowIfNull(column);
        _cell = cell ?? throw new ArgumentNullException(nameof(cell));
        Name = column.Name;
        DataTypeName = column.DataTypeName;
        _cell.PropertyChanged += OnCellChanged;
    }

    public string Name { get; }

    public string DataTypeName { get; }

    public string? Text => _cell.Text;

    public bool IsNull => _cell.IsNull;

    /// <summary>Booleans and read-only cells stay display-only; the grid owns them.</summary>
    public bool CanEdit => _cell.UsesTextEditor;

    public bool ShowsEditAction => CanEdit && !IsEditing;

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(ShowsEditAction));
            }
        }
    }

    public string Draft
    {
        get => _draft;
        set => SetProperty(ref _draft, value ?? string.Empty);
    }

    public string? ValidationError => _cell.ValidationError;

    public bool HasValidationError => !string.IsNullOrWhiteSpace(_cell.ValidationError);

    private string? _grammarSource;
    private string? _grammarExtension;

    /// <summary>
    /// The grammar this value reads as — declared kind first, then a bounded
    /// sniff for plain text. Cached per text instance so grid keystrokes do
    /// not re-parse the value.
    /// </summary>
    public string? GrammarExtension
    {
        get
        {
            var text = _cell.IsNull ? null : _cell.Text;
            if (!ReferenceEquals(_grammarSource, text))
            {
                _grammarSource = text;
                _grammarExtension = DatabaseValueGrammar.DetectExtension(
                    _cell.Column.ValueKind,
                    text);
            }

            return _grammarExtension;
        }
    }

    public bool HasGrammar => GrammarExtension is not null;

    /// <summary>The synthetic name the preview's grammar registry resolves.</summary>
    public string? GrammarFileName =>
        GrammarExtension is { } extension ? $"value{extension}" : null;

    public void BeginEdit()
    {
        if (!CanEdit)
        {
            return;
        }

        Draft = _cell.IsNull ? string.Empty : _cell.EditText;
        IsEditing = true;
    }

    /// <summary>Stages the draft into the cell — the same move as typing in the grid.</summary>
    public void ApplyEdit()
    {
        if (!IsEditing)
        {
            return;
        }

        _cell.EditText = Draft;
        IsEditing = false;
    }

    public void CancelEdit() => IsEditing = false;

    private void OnCellChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        switch (e.PropertyName)
        {
            case null:
            case nameof(DatabaseResultCellViewModel.Text):
            case nameof(DatabaseResultCellViewModel.State):
                OnPropertyChanged(nameof(Text));
                OnPropertyChanged(nameof(IsNull));
                OnPropertyChanged(nameof(GrammarExtension));
                OnPropertyChanged(nameof(HasGrammar));
                OnPropertyChanged(nameof(GrammarFileName));
                break;
            case nameof(DatabaseResultCellViewModel.ValidationError):
                OnPropertyChanged(nameof(ValidationError));
                OnPropertyChanged(nameof(HasValidationError));
                break;
        }
    }

    public void Dispose() => _cell.PropertyChanged -= OnCellChanged;
}

public sealed class DatabaseResultColumnViewModel(
    DatabaseColumnDescriptor column,
    double width,
    bool canEdit = false,
    bool? sortDescending = null)
{
    public DatabaseColumnDescriptor Descriptor { get; } = column;

    public string Name { get; } = column.Name;

    public string DataTypeName { get; } = column.DataTypeName;

    public DatabaseValueKind ValueKind { get; } = column.ValueKind;

    public bool IsEditable { get; } = canEdit
        && !column.IsReadOnly
        && column.ValueKind is not (DatabaseValueKind.Other
            or DatabaseValueKind.Binary
            or DatabaseValueKind.Collection
            or DatabaseValueKind.Json
            or DatabaseValueKind.Network);

    public bool? SortDescending { get; } = sortDescending;

    public double Width { get; } = width;
}

public sealed class DatabaseStructureColumnViewModel(DatabaseColumnSchema column)
{
    public string Name { get; } = column.Name;

    public string Type { get; } = FormatType(column);

    public string Nullable { get; } = column.IsNullable switch
    {
        true => "Yes",
        false => "No",
        null => "Unknown",
    };

    public string Key { get; } = column.IsPrimaryKey
        ? $"PK {column.PrimaryKeyOrdinal}"
        : string.Empty;

    public string Default { get; } = column.DefaultExpression ?? string.Empty;

    public string Flags { get; } = string.Join(", ", new[]
    {
        column.IsIdentity ? "identity" : null,
        column.IsGenerated ? "generated" : null,
        column.IsReadOnly ? "read-only" : null,
    }.Where(value => value is not null));

    private static string FormatType(DatabaseColumnSchema value)
    {
        if (value.DataTypeName.Contains('(', StringComparison.Ordinal))
        {
            return value.DataTypeName;
        }

        // Several catalogs report zero precision/scale for non-numeric types
        // (notably SQL Server nvarchar). In that case the declared length is
        // the meaningful shape; rendering (0, 0) hides it from the user.
        if (value.Precision is int precision && precision > 0)
        {
            return value.Scale is { } scale
                ? $"{value.DataTypeName}({precision}, {scale})"
                : $"{value.DataTypeName}({precision})";
        }

        return value.Length is { } length
            ? length < 0
                ? $"{value.DataTypeName}(max)"
                : $"{value.DataTypeName}({length})"
            : value.DataTypeName;
    }
}

public sealed class DatabaseIndexViewModel(DatabaseIndexSchema index)
{
    public string Name { get; } = index.Name;

    public string Kind { get; } = index.Kind;

    public string Columns { get; } = string.Join(", ", index.Columns.Select(FormatColumn));

    public string Unique { get; } = index.IsUnique ? "Yes" : "No";

    public string Status { get; } = index.IsValid ? "Valid" : "Unavailable";

    public string Predicate { get; } = index.Predicate
        ?? index.Details?.GetValueOrDefault("Definition")
        ?? string.Empty;

    private static string FormatColumn(DatabaseIndexColumn column)
    {
        var value = column.Name ?? column.Expression ?? "expression";
        if (column.IsDescending)
        {
            value += " DESC";
        }

        return column.IsIncluded ? value + " INCLUDE" : value;
    }
}

public sealed class DatabaseFilterColumnViewModel(DatabaseColumnSchema column)
{
    public DatabaseColumnSchema Column { get; } = column;

    public string Name { get; } = column.Name;

    public DatabaseValueKind ValueKind { get; } = column.ValueKind;
}

public sealed record DatabaseFilterOperatorViewModel(
    DatabaseFilterOperator Operator,
    string Label);

/// <summary>
/// One condition in the stackable filter bar. A row can be switched off with
/// its checkbox without losing what it says; applying reads every included,
/// complete row. Operators follow the chosen column's semantic type.
/// </summary>
public sealed class DatabaseFilterRowViewModel : ObservableObject
{
    private readonly Func<DatabaseValueKind?, IReadOnlyList<DatabaseFilterOperatorViewModel>>
        _operatorsFor;
    private IReadOnlyList<DatabaseFilterOperatorViewModel> _operators;
    private DatabaseFilterColumnViewModel? _column;
    private DatabaseFilterOperatorViewModel? _operator;
    private string _value = string.Empty;
    private bool _isIncluded = true;

    public DatabaseFilterRowViewModel(
        IReadOnlyList<DatabaseFilterColumnViewModel> columns,
        Func<DatabaseValueKind?, IReadOnlyList<DatabaseFilterOperatorViewModel>> operatorsFor)
    {
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        _operatorsFor = operatorsFor ?? throw new ArgumentNullException(nameof(operatorsFor));
        _operators = operatorsFor(null);
    }

    public IReadOnlyList<DatabaseFilterColumnViewModel> Columns { get; }

    public IReadOnlyList<DatabaseFilterOperatorViewModel> Operators
    {
        get => _operators;
        private set => SetProperty(ref _operators, value);
    }

    /// <summary>An unchecked row keeps its condition but stays out of the query.</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    public DatabaseFilterColumnViewModel? Column
    {
        get => _column;
        set
        {
            if (!SetProperty(ref _column, value))
            {
                return;
            }

            Operators = _operatorsFor(value?.ValueKind);
            if (Operator is null
                || Operators.All(option => option.Operator != Operator.Operator))
            {
                Operator = Operators[0];
            }
        }
    }

    public DatabaseFilterOperatorViewModel? Operator
    {
        get => _operator;
        set
        {
            if (SetProperty(ref _operator, value))
            {
                OnPropertyChanged(nameof(NeedsValue));
            }
        }
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    public bool NeedsValue => Operator?.Operator is not
        (DatabaseFilterOperator.IsNull or DatabaseFilterOperator.IsNotNull);

    /// <summary>Says something the query can use.</summary>
    public bool IsComplete => Column is not null && Operator is not null;
}
