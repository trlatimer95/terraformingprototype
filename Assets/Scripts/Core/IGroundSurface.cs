using UnityEngine;

namespace Terraform.Core
{
    /// <summary>
    /// Everything the span store needs to know about the ground above it.
    ///
    /// The hybrid does not care which surface model it is joined to. It needs a height and a
    /// shading normal at an arbitrary point, and it needs to know which way each patch of
    /// surface folds into triangles -- and nothing else. Putting that behind an interface is
    /// what lets the same underground, the same ceding rule and the same seam serve a
    /// custom-mesh surface and a native Unity Terrain surface without either knowing about
    /// the other.
    ///
    /// It also keeps the comparison honest. If the two surface modes shared no code the
    /// difference between them would include every incidental difference in how they were
    /// written; sharing everything below this line means what is left really is the surface.
    /// </summary>
    public interface IGroundSurface
    {
        int CellsX { get; }
        int CellsZ { get; }
        float CellSize { get; }
        Vector3 Origin { get; }

        /// <summary>Bumped on every edit, so cached derivatives know to rebuild.</summary>
        int Version { get; }

        /// <summary>Height in metres at a point in cell units, above the origin.</summary>
        float MetresAt(float gx, float gz);

        /// <summary>Shading normal at a point in cell units.</summary>
        Vector3 NormalAt(float gx, float gz);

        /// <summary>
        /// Which way a span column's surface cap must fold, given its position within its
        /// cell. A quad with four corner heights is not a surface until you say how it folds,
        /// and folding against the crease flattens detail the surface really has.
        /// </summary>
        bool SwapCapDiagonal(int cx, int cz, int ix, int iz, int columnsPerCell);
    }

    /// <summary>The cell model as a ground surface: one stored height per cell, corners derived.</summary>
    public sealed class CellGround : IGroundSurface
    {
        public readonly CellGrid Grid;

        public CellGround(CellGrid grid) { Grid = grid; }

        public int CellsX { get { return Grid.CellsX; } }
        public int CellsZ { get { return Grid.CellsZ; } }
        public float CellSize { get { return Grid.CellSize; } }
        public Vector3 Origin { get { return Grid.Origin; } }
        public int Version { get { return Grid.Version; } }

        public float MetresAt(float gx, float gz) { return CellSurface.MetresAt(Grid, gx, gz); }
        public Vector3 NormalAt(float gx, float gz) { return CellSurface.NormalAt(Grid, gx, gz); }

        /// <summary>
        /// A cell fans eight triangles from its centre, so it creases along BOTH diagonals.
        /// The columns on the anti-diagonal are the ones that need the other fold.
        /// </summary>
        public bool SwapCapDiagonal(int cx, int cz, int ix, int iz, int columnsPerCell)
        {
            return ix + iz == columnsPerCell - 1;
        }
    }

    /// <summary>The vertex model as a ground surface: one height per shared vertex.</summary>
    public sealed class VertexGround : IGroundSurface
    {
        public readonly HeightGrid Grid;

        public VertexGround(HeightGrid grid) { Grid = grid; }

        public int CellsX { get { return Grid.CellsX; } }
        public int CellsZ { get { return Grid.CellsZ; } }
        public float CellSize { get { return Grid.CellSize; } }
        public Vector3 Origin { get { return Grid.Origin; } }
        public int Version { get { return Grid.Version; } }

        public float MetresAt(float gx, float gz) { return VertexSurface.MetresAt(Grid, gx, gz); }
        public Vector3 NormalAt(float gx, float gz) { return VertexSurface.NormalAt(Grid, gx, gz); }

        /// <summary>
        /// A cell is two triangles split along one diagonal, so the whole cell folds one way.
        /// Columns that do not straddle that diagonal are flat either way, which is why the
        /// answer can ignore where in the cell the column sits.
        /// </summary>
        public bool SwapCapDiagonal(int cx, int cz, int ix, int iz, int columnsPerCell)
        {
            return VertexSurface.AntiDiagonal(Grid, cx, cz);
        }
    }
}
