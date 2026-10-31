using System;
using Unity.Collections;
using Unity.Mathematics;

/// <summary>
/// Five scalar element-density grids (one per element) plus a sixth field holding the
/// density of all particles together, used for short-range pressure. Values live at cell
/// centers; particles deposit into four nearest centers and sample the gradients the same
/// way. The grid wraps at the world border for now.
/// </summary>
public sealed class QiFieldGrid : IDisposable
{
    public static int Width = 100;
    public static int Height = 100;
    public static int CellCount => Width * Height;

    /// <summary>
    /// Extra scalar field, after the five element fields. It is filled by every particle
    /// and used only for short-range pressure, not for element attraction.
    /// </summary>
    public const int PressureField = QiParticleData.ElementCount;

    public const int FieldCount = QiParticleData.ElementCount + 1;

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

    float[] _pressureKernel = Array.Empty<float>();
    int _pressureKernelRadius;
    float _pressureKernelSigma = -1f;

    int[] _wrapRows = Array.Empty<int>();

    public bool IsCreated => _density != null && _density.Length > 0 && _density[0].IsCreated;

    public static void Configure(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public void Allocate()
    {
        Dispose();

        int fields = FieldCount;
        _density = new NativeArray<float>[fields];
        _gradientX = new NativeArray<float>[fields];
        _gradientY = new NativeArray<float>[fields];

        for (int field = 0; field < fields; field++)
        {
            _density[field] = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _gradientX[field] = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _gradientY[field] = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        _blurTemp = new NativeArray<float>(CellCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
    }

    public void Clear()
    {
        if (!IsCreated)
        {
            return;
        }

        for (int field = 0; field < _density.Length; field++)
        {
            NativeArray<float> cells = _density[field];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = 0f;
            }
        }
    }

    /// <summary>
    /// Spreads the particle weight over four cell centers. <paramref name="field"/> is either
    /// an element index or <see cref="PressureField"/>. Values are stored at centers,
    /// so the grid coordinate is shifted by half a cell.
    /// </summary>
    public void Deposit(int field, float2 worldPosition, float weight)
    {
        if (!IsCreated || field < 0 || field >= _density.Length)
        {
            return;
        }

        float2 gridPosition = WorldToGrid(worldPosition);
        int x0 = (int)math.floor(gridPosition.x);
        int y0 = (int)math.floor(gridPosition.y);
        float fx = gridPosition.x - x0;
        float fy = gridPosition.y - y0;

        int x1 = x0 + 1;
        int y1 = y0 + 1;

        NativeArray<float> cells = _density[field];
        int ix0y0 = ToIndex(FastWrap(x0, Width), FastWrap(y0, Height));
        int ix1y0 = ToIndex(FastWrap(x1, Width), FastWrap(y0, Height));
        int ix0y1 = ToIndex(FastWrap(x0, Width), FastWrap(y1, Height));
        int ix1y1 = ToIndex(FastWrap(x1, Width), FastWrap(y1, Height));

        cells[ix0y0] += weight * (1f - fx) * (1f - fy);
        cells[ix1y0] += weight * fx * (1f - fy);
        cells[ix0y1] += weight * (1f - fx) * fy;
        cells[ix1y1] += weight * fx * fy;
    }

    /// <summary>
    /// Fills the pressure field with the sum of the five element fields. Every particle
    /// is already counted in exactly one element field, so this avoids a sixth deposit
    /// pass while still including all particles.
    /// </summary>
    public void BuildPressureField()
    {
        if (!IsCreated)
        {
            return;
        }

        NativeArray<float> pressure = _density[PressureField];
        NativeArray<float> first = _density[0];
        for (int i = 0; i < pressure.Length; i++)
        {
            pressure[i] = first[i];
        }

        for (int field = 1; field < QiParticleData.ElementCount; field++)
        {
            NativeArray<float> cells = _density[field];
            for (int i = 0; i < pressure.Length; i++)
            {
                pressure[i] += cells[i];
            }
        }
    }

    /// <summary>
    /// Separable Gaussian blur of the five element fields. Sigma is measured in cells.
    /// The cost depends on the grid size, not on the particle count.
    /// </summary>
    public void Blur(float sigmaCells)
    {
        if (!IsCreated || sigmaCells <= 0.01f)
        {
            return;
        }

        EnsureKernel(ref _kernel, ref _kernelRadius, ref _kernelSigma, sigmaCells);

        for (int field = 0; field < QiParticleData.ElementCount; field++)
        {
            BlurField(_density[field], _kernel, _kernelRadius);
        }
    }

    /// <summary>
    /// Blurs only the all-particle pressure field. It has its own sigma, usually much
    /// smaller than the element blur, because pressure must stay short-ranged.
    /// </summary>
    public void BlurPressure(float sigmaCells)
    {
        if (!IsCreated || sigmaCells <= 0.01f)
        {
            return;
        }

        EnsureKernel(ref _pressureKernel, ref _pressureKernelRadius, ref _pressureKernelSigma, sigmaCells);
        BlurField(_density[PressureField], _pressureKernel, _pressureKernelRadius);
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

        for (int field = 0; field < _density.Length; field++)
        {
            NativeArray<float> cells = _density[field];
            NativeArray<float> gradientX = _gradientX[field];
            NativeArray<float> gradientY = _gradientY[field];

            for (int y = 0; y < Height; y++)
            {
                int row = y * Width;
                int rowUp = WrapIndex(y + 1, Height) * Width;
                int rowDown = WrapIndex(y - 1, Height) * Width;

                for (int x = 0; x < Width; x++)
                {
                    int index = row + x;
                    int rx = x + 1;
                    int lx = x - 1;
                    if ((uint)rx >= (uint)Width)
                    {
                        rx = WrapIndex(rx, Width);
                    }

                    if ((uint)lx >= (uint)Width)
                    {
                        lx = WrapIndex(lx, Width);
                    }

                    float right = cells[row + rx];
                    float left = cells[row + lx];
                    float up = cells[rowUp + x];
                    float down = cells[rowDown + x];

                    gradientX[index] = (right - left) * invCell;
                    gradientY[index] = (up - down) * invCell;
                }
            }
        }
    }

    public float2 SampleGradient(int field, float2 worldPosition)
    {
        if (!IsCreated || field < 0 || field >= _gradientX.Length)
        {
            return float2.zero;
        }

        float2 gridPosition = WorldToGrid(worldPosition);
        int x0 = (int)math.floor(gridPosition.x);
        int y0 = (int)math.floor(gridPosition.y);
        float fx = gridPosition.x - x0;
        float fy = gridPosition.y - y0;

        int x1 = x0 + 1;
        int y1 = y0 + 1;

        int wx0 = FastWrap(x0, Width);
        int wx1 = FastWrap(x1, Width);
        int wy0 = FastWrap(y0, Height);
        int wy1 = FastWrap(y1, Height);

        NativeArray<float> gradientX = _gradientX[field];
        NativeArray<float> gradientY = _gradientY[field];

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

    void EnsureKernel(ref float[] kernel, ref int kernelRadius, ref float kernelSigma, float sigmaCells)
    {
        if (math.abs(kernelSigma - sigmaCells) < 1e-4f && kernel.Length > 0)
        {
            return;
        }

        kernelRadius = math.max(1, (int)math.ceil(sigmaCells * 3f));
        int size = kernelRadius * 2 + 1;
        if (kernel.Length != size)
        {
            kernel = new float[size];
        }

        float invTwoSigmaSquared = 1f / (2f * sigmaCells * sigmaCells);
        float sum = 0f;

        for (int i = 0; i < size; i++)
        {
            float offset = i - kernelRadius;
            float weight = math.exp(-offset * offset * invTwoSigmaSquared);
            kernel[i] = weight;
            sum += weight;
        }

        float invSum = 1f / sum;
        for (int i = 0; i < size; i++)
        {
            kernel[i] *= invSum;
        }

        kernelSigma = sigmaCells;
    }

    void BlurField(NativeArray<float> cells, float[] kernel, int kernelRadius)
    {
        BlurHorizontal(cells, kernel, kernelRadius);
        BlurVertical(cells, kernel, kernelRadius);
    }

    void BlurHorizontal(NativeArray<float> cells, float[] kernel, int kernelRadius)
    {
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                float sum = 0f;
                for (int k = -kernelRadius; k <= kernelRadius; k++)
                {
                    int sampleX = x + k;
                    if ((uint)sampleX >= (uint)Width)
                    {
                        sampleX = WrapIndex(sampleX, Width);
                    }

                    sum += cells[row + sampleX] * kernel[k + kernelRadius];
                }

                _blurTemp[row + x] = sum;
            }
        }
    }

    void BlurVertical(NativeArray<float> cells, float[] kernel, int kernelRadius)
    {
        int size = kernelRadius * 2 + 1;
        if (_wrapRows.Length < size)
        {
            _wrapRows = new int[size];
        }

        for (int y = 0; y < Height; y++)
        {
            // Row wrapping is independent of x, so it is resolved once per row instead
            // of once per sampled cell.
            for (int k = -kernelRadius; k <= kernelRadius; k++)
            {
                _wrapRows[k + kernelRadius] = FastWrap(y + k, Height) * Width;
            }

            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                float sum = 0f;
                for (int k = -kernelRadius; k <= kernelRadius; k++)
                {
                    sum += _blurTemp[_wrapRows[k + kernelRadius] + x] * kernel[k + kernelRadius];
                }

                cells[row + x] = sum;
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

    /// <summary>
    /// Same result as <see cref="WrapIndex"/>, but the common in-range case costs a single
    /// compare instead of an integer division. The modulo fallback keeps it correct for any
    /// input, so callers near the border do not need their own edge handling.
    /// </summary>
    public static int FastWrap(int value, int size)
    {
        if ((uint)value < (uint)size)
        {
            return value;
        }

        return WrapIndex(value, size);
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
