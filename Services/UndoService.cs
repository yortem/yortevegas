using YorteVegas.ViewModels;

namespace YorteVegas.Services;

/// <summary>
/// Snapshot undo/redo: captures the whole project before each mutation (drag
/// gestures capture once, at gesture start) and restores it losslessly.
/// Bounded so long sessions cannot grow without limit.
/// </summary>
public sealed class UndoService
{
    private sealed record Entry(ProjectFile State);

    private readonly LinkedList<Entry> _undo = new();
    private readonly LinkedList<Entry> _redo = new();

    public int Limit { get; set; } = 50;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised whenever the available history changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Record the pre-mutation state. Clears the redo stack.</summary>
    public void Capture(EditProject project)
    {
        _undo.AddFirst(new Entry(ProjectService.Capture(project)));
        while (_undo.Count > Limit) _undo.RemoveLast();
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo(EditProject project)
    {
        if (_undo.First == null) return false;
        _redo.AddFirst(new Entry(ProjectService.Capture(project)));
        var state = _undo.First.Value;
        _undo.RemoveFirst();
        ProjectService.Apply(state.State, project);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo(EditProject project)
    {
        if (_redo.First == null) return false;
        _undo.AddFirst(new Entry(ProjectService.Capture(project)));
        var state = _redo.First.Value;
        _redo.RemoveFirst();
        ProjectService.Apply(state.State, project);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
