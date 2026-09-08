using System.Collections.Generic;

namespace Terraform.Core
{
    /// <summary>
    /// The single funnel for terrain mutation. Singleplayer applies immediately; the
    /// same log is what a server validates and replicates later, and what a save file
    /// stores instead of raw heightmaps. Undo is a side effect of getting that right.
    /// </summary>
    public sealed class CommandLog
    {
        readonly List<ITerrainCommand> _done = new List<ITerrainCommand>();
        readonly List<ITerrainCommand> _undone = new List<ITerrainCommand>();

        public int DoneCount { get { return _done.Count; } }
        public int UndoneCount { get { return _undone.Count; } }

        /// <summary>Running cut/fill balance in cubic metres. Positive means net fill.</summary>
        public float NetVolume { get; private set; }

        public string LastDescription { get; private set; }

        public bool Execute(ITerrainCommand cmd, HeightGrid grid)
        {
            if (!cmd.Validate(grid)) return false;

            cmd.Apply(grid);
            _done.Add(cmd);
            _undone.Clear();          // new work invalidates the redo branch
            NetVolume += cmd.SignedVolume;
            LastDescription = cmd.Describe();
            return true;
        }

        public bool Undo(HeightGrid grid)
        {
            if (_done.Count == 0) return false;

            ITerrainCommand cmd = _done[_done.Count - 1];
            _done.RemoveAt(_done.Count - 1);
            cmd.Revert(grid);
            _undone.Add(cmd);
            NetVolume -= cmd.SignedVolume;
            LastDescription = "undo: " + cmd.Describe();
            return true;
        }

        public bool Redo(HeightGrid grid)
        {
            if (_undone.Count == 0) return false;

            ITerrainCommand cmd = _undone[_undone.Count - 1];
            _undone.RemoveAt(_undone.Count - 1);
            if (!cmd.Validate(grid)) return false;

            cmd.Apply(grid);
            _done.Add(cmd);
            NetVolume += cmd.SignedVolume;
            LastDescription = "redo: " + cmd.Describe();
            return true;
        }

        public void Clear()
        {
            _done.Clear();
            _undone.Clear();
            NetVolume = 0f;
            LastDescription = null;
        }
    }
}
