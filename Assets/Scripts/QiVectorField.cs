using System;
using Unity.Collections;
using Unity.Mathematics;

/// <summary>
/// A float2 flow grid used for temporary velocity fields such as the pointer trail.
/// Values live at cell centers, are sampled bilinearly, and decay exponentially.
/// </summary>
public sealed class QiVectorField : IDisposable
{
    NativeArray<float2> _cells;

    public bool IsCreated => _cells.IsCreated;

    public void Allocate()
    {
        Dispose();
        _cells = new NativeArray<float2>(QiFieldGrid.CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
    }

    public void Clear()
    {
        if (!_cells.IsCreated)
        {
            return;
        }

        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = float2.zero;
        }
    }

    public void Decay(float rate, float deltaTime)
    {
        if (!_cells.IsCreated || rate <= 0f || deltaTime <= 0f)
        {
            return;
        }

        float factor = math.exp(-rate * deltaTime);
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] *= factor;
        }
    }

    /// <summary>
    /// Blends a compact Gaussian splat of a flow value around a world position.
    /// Only the pointer uses this; density fields never stamp per particle.
    /// </summary>
    public void Stamp(float2 worldPosition, float2 value, float sigmaCells)
    {
        if (!_cells.IsCreated || sigmaCells <= 0.01f)
        {
            return;
        }

        float2 gridPosition = QiFieldGrid.WorldToGrid(worldPosition);
        int centerX = (int)math.round(gridPosition.x);
        int centerY = (int)math.round(gridPosition.y);
        int radius = math.max(1, (int)math.ceil(sigmaCells * 3f));
        float invTwoSigmaSquared = 1f / (2f * sigmaCells * sigmaCells);

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                float distanceSquared = (float)(dx * dx + dy * dy);
                float weight = math.exp(-distanceSquared * invTwoSigmaSquared);
                int x = QiFieldGrid.WrapIndex(centerX + dx, QiFieldGrid.Width);
                int y = QiFieldGrid.WrapIndex(centerY + dy, QiFieldGrid.Height);
                int index = y * QiFieldGrid.Width + x;
                _cells[index] = math.lerp(_cells[index], value, weight);
            }
        }
    }

    public float2 Sample(float2 worldPosition)
    {
        if (!_cells.IsCreated)
        {
            return float2.zero;
        }

        float2 gridPosition = QiFieldGrid.WorldToGrid(worldPosition);
        int x0 = (int)math.floor(gridPosition.x);
        int y0 = (int)math.floor(gridPosition.y);
        float fx = gridPosition.x - x0;
        float fy = gridPosition.y - y0;

        int x1 = x0 + 1;
        int y1 = y0 + 1;

        int wx0 = QiFieldGrid.WrapIndex(x0, QiFieldGrid.Width);
        int wx1 = QiFieldGrid.WrapIndex(x1, QiFieldGrid.Width);
        int wy0 = QiFieldGrid.WrapIndex(y0, QiFieldGrid.Height);
        int wy1 = QiFieldGrid.WrapIndex(y1, QiFieldGrid.Height);

        float2 v00 = _cells[wy0 * QiFieldGrid.Width + wx0];
        float2 v10 = _cells[wy0 * QiFieldGrid.Width + wx1];
        float2 v01 = _cells[wy1 * QiFieldGrid.Width + wx0];
        float2 v11 = _cells[wy1 * QiFieldGrid.Width + wx1];

        float2 top = math.lerp(v00, v10, fx);
        float2 bottom = math.lerp(v01, v11, fx);
        return math.lerp(top, bottom, fy);
    }

    public void Dispose()
    {
        if (_cells.IsCreated)
        {
            _cells.Dispose();
        }
    }
}
