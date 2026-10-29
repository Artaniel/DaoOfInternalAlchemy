using System;
using Unity.Collections;
using Unity.Mathematics;

/// <summary>
/// Five scalar element-density grids (one per element) plus their gradients.
/// Values live at cell centers; particles deposit into four nearest centers and
/// sample the gradients the same way. The grid wraps at the world border for now.
/// </summary>
public sealed class QiFieldGrid : IDisposable
{
    public static int Width = 100;
    public static int Height = 100;
    public static int CellCount => Width * Height;

    public const float WorldSize = 10f;
    public static float WorldHalfSize => WorldSize * 0.5f;
    public static float CellSize => WorldSize / Width;

    NativeArray<float>[] _density;
    NativeArray<float>[] _gradientX;
    NativeArray<float>[] _gradientY;
    NativeArray<float> _blurTemp;

    float[] _kernel = Array.Empty<float>();
    int _kernelRadius;
    float _kernelSigma = -1f;

    public bool IsCreated => _density != null && _density.Length > 0 && _density[0].IsCreated;

    public static void Configure(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public void Allocate()
    {
        Dispose();

        int elements = QiParticleData.ElementCount;
        _density = new NativeArray<float>[elements];
        _gradientX = new NativeArray<float>[elements];
        _gradientY = new NativeArray<float>[elements];

        for (int element = 0; element < elements; element++)
        {
            _density[element] = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _gradientX[element] = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _gradientY[element] = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        _blurTemp = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
    }

    public void Clear()
    {
        if (!IsCreated)
        {
            return;
        }

        for (int element = 0; element < _density.Length; element++)
        {
            NativeArray<float> cells = _density[element];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = 0f;
            }
        }
    }

    /// <summary>
    /// Spreads the particle weight over four cell centers. Values are stored at centers,
    /// so the grid coordinate is shifted by half a cell.
    /// </summary>
    public void Deposit(int element, float2 worldPosition, float weight)
    {
        float2 gridPosition = WorldToGrid(worldPosition);
        int x0 = (int)math.floor(gridPosition.x);
        int y0 = (int)math.floor(gridPosition.y);
        float fx = gridPosition.x - x0;
        float fy = gridPosition.y - y0;

        int x1 = x0 + 1;
        int y1 = y0 + 1;

        NativeArray<float> cells = _density[element];
        cells[ToIndex(WrapIndex(x0, Width), WrapIndex(y0, Height))] += weight * (1f - fx) * (1f - fy);
        cells[ToIndex(WrapIndex(x1, Width), WrapIndex(y0, Height))] += weight * fx * (1f - fy);
        cells[ToIndex(WrapIndex(x0, Width), WrapIndex(y1, Height))] += weight * (1f - fx) * fy;
        cells[ToIndex(WrapIndex(x1, Width), WrapIndex(y1, Height))] += weight * fx * fy;
    }

    /// <summary>
    /// Separable Gaussian blur. Sigma is measured in cells. The cost depends on the
    /// grid size, not on the particle count.
    /// </summary>
    public void Blur(float sigmaCells)
    {
        if (!IsCreated || sigmaCells <= 0.01f)
        {
            return;
        }

        EnsureKernel(sigmaCells);

        for (int element = 0; element < _density.Length; element++)
        {
            NativeArray<float> cells = _density[element];
            BlurHorizontal(cells);
            BlurVertical(cells);
        }
    }

    /// <summary>
    /// Central differences between neighbouring cell centers. Gradients are stored
    /// per cell and sampled bilinearly by particles.
    /// </summary>
    public void ComputeGradients()
    {
        if (!IsCreated)
        {
            return;
        }

        float invCell = 0.5f / CellSize;

        for (int element = 0; element < _density.Length; element++)
        {
            NativeArray<float> cells = _density[element];
            NativeArray<float> gradientX = _gradientX[element];
            NativeArray<float> gradientY = _gradientY[element];

            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                int rowUp = WrapIndex(y + 1, Height) * Width;
                int rowDown = WrapIndex(y - 1, Height) * Width;

                for (int x = 0; x < Width; x++)
                {
                    int index = row + x;
                    float right = cells[row + WrapIndex(x + 1, Width)];
                    float left = cells[row + WrapIndex(x - 1, Width)];
                    float up = cells[rowUp + x];
                    float down = cells[rowDown + x];

                    gradientX[index] = (right - left) * invCell;
                    gradientY[index] = (up - down) * invCell;
                }
            }
        }
    }

    public float2 SampleGradient(int element, float2 worldPosition)
    {
        float2 gridPosition = WorldToGrid(worldPosition);
        int x0 = (int)math.floor(gridPosition.x);
        int y0 = (int)math.floor(gridPosition.y);
        float fx = gridPosition.x - x0;
        float fy = gridPosition.y - y0;

        int x1 = x0 + 1;
        int y1 = y0 + 1;

        int wx0 = WrapIndex(x0, Width);
        int wx1 = WrapIndex(x1, Width);
        int wy0 = WrapIndex(y0, Height);
        int wy1 = WrapIndex(y1, Height);

        NativeArray<float> gradientX = _gradientX[element];
        NativeArray<float> gradientY = _gradientY[element];

        float gx0 = math.lerp(
            gradientX[ToIndex(wx0, wy0)],
            gradientX[ToIndex(wx1, wy0)],
            fx);
        float gx1 = math.lerp(
            gradientX[ToIndex(wx0, wy1)],
            gradientX[ToIndex(wx1, wy1)],
            fx);

        float gy0 = math.lerp(
            gradientY[ToIndex(wx0, wy0)],
            gradientY[ToIndex(wx1, wy0)],
            fx);
        float gy1 = math.lerp(
            gradientY[ToIndex(wx0, wy1)],
            gradientY[ToIndex(wx1, wy1)],
            fx);

        return new float2(math.lerp(gx0, gx1, fy), math.lerp(gy0, gy1, fy));
    }

    void EnsureKernel(float sigmaCells)
    {
        if (math.abs(_kernelSigma - sigmaCells) < 1e-4f && _kernel.Length > 0)
        {
            return;
        }

        _kernelRadius = math.max(1, (int)math.ceil(sigmaCells * 3f));
        int size = _kernelRadius * 2 + 1;
        if (_kernel.Length != size)
        {
            _kernel = new float[size];
        }

        float invTwoSigmaSquared = 1f / (2f * sigmaCells * sigmaCells);
        float sum = 0f;

        for (int i = 0; i < size; i++)
        {
            float offset = i - _kernelRadius;
            float weight = math.exp(-offset * offset * invTwoSigmaSquared);
            _kernel[i] = weight;
            sum += weight;
        }

        float invSum = 1f / sum;
        for (int i = 0; i < size; i++)
        {
            _kernel[i] *= invSum;
        }

        _kernelSigma = sigmaCells;
    }

    void BlurHorizontal(NativeArray<float> cells)
    {
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                float sum = 0f;
                for (int k = -_kernelRadius; k <= _kernelRadius; k++)
                {
                    sum += cells[row + WrapIndex(x + k, Width)] * _kernel[k + _kernelRadius];
                }

                _blurTemp[row + x] = sum;
            }
        }
    }

    void BlurVertical(NativeArray<float> cells)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float sum = 0f;
                for (int k = -_kernelRadius; k <= _kernelRadius; k++)
                {
                    int sampleY = WrapIndex(y + k, Height);
                    sum += _blurTemp[sampleY * Width + x] * _kernel[k + _kernelRadius];
                }

                cells[y * Width + x] = sum;
            }
        }
    }

    public int ToIndex(int x, int y) => y * Width + x;

    /// <summary>
    /// Grid coordinates of cell centers: integer values sit at the center of a cell.
    /// </summary>
    public static float2 WorldToGrid(float2 world) =>
        new float2(
            (world.x + WorldHalfSize) / CellSize - 0.5f,
            (world.y + WorldHalfSize) / CellSize - 0.5f);

    public static float2 GridToWorld(float2 grid) =>
        new float2(
            (grid.x + 0.5f) * CellSize - WorldHalfSize,
            (grid.y + 0.5f) * CellSize - WorldHalfSize);

    public static int WrapIndex(int value, int size)
    {
        int wrapped = value % size;
        return wrapped < 0 ? wrapped + size : wrapped;
    }

    public void Dispose()
    {
        if (_density != null)
        {
            for (int element = 0; element < _density.Length; element++)
            {
                Dispose(ref _density[element]);
                Dispose(ref _gradientX[element]);
                Dispose(ref _gradientY[element]);
            }

            _density = null;
            _gradientX = null;
            _gradientY = null;
        }

        if (_blurTemp.IsCreated)
        {
            _blurTemp.Dispose();
        }
    }

    static void Dispose(ref NativeArray<float> array)
    {
        if (array.IsCreated)
        {
            array.Dispose();
        }
    }
}
