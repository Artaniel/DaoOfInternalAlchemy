using System;
using Unity.Collections;

public sealed class QiFieldGrid : IDisposable
{
    public const int Width = 1000;
    public const int Height = 1000;
    public const int CellCount = Width * Height;

    NativeArray<float> _cells;

    public bool IsCreated => _cells.IsCreated;

    public NativeArray<float> Cells => _cells;

    public void Allocate()
    {
        if (_cells.IsCreated)
        {
            return;
        }

        _cells = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
    }

    public int ToIndex(int x, int y) => y * Width + x;

    public float Get(int x, int y) => _cells[ToIndex(x, y)];

    public void Set(int x, int y, float value) => _cells[ToIndex(x, y)] = value;

    public void Clear()
    {
        if (!_cells.IsCreated)
        {
            return;
        }

        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = 0f;
        }
    }

    public void Dispose()
    {
        if (_cells.IsCreated)
        {
            _cells.Dispose();
        }
    }
}
