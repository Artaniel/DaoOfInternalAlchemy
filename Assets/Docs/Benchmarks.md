# Нагрузочные замеры

Два независимых прогона на одном и том же рендере:

- **Замер A — статичные квады.** Baseline до всякой симуляции: частицы стоят, `Tick` ещё не было.
- **Замер B — с движением (S1).** `Tick(dt)`, случайная скорость, torus-wrap.

Замер A нужен, чтобы видеть цену рендера отдельно от цены симуляции. Замер B показывает, что
стоит движение на каждом шаге нагрузки.

## Условия

- Unity `6000.6.0f1`, URP 2D, Editor Play Mode (не билд).
- Windows 10 22H2, Intel Xeon E5-2680 v3 (12 ядер / 24 потока), NVIDIA Quadro M4000, 32 ГБ RAM.
- Game view `960 × 600`, vsync off, `Application.runInBackground = true` (иначе редактор
  перестаёт тикать кадры при потере фокуса и замер получает 0 кадров).
- Частицы: белый unlit-квад `0.15` world units.
- Спавн: кольцо `1.5–3.5` world units, поле `1000 × 1000` выделено, но не используется.
- Рендер: `Graphics.RenderMeshInstanced`, батчи по 511 инстансов.
- Прогрев 1.5 с, затем замер до 60 кадров или 2.5 с (что наступит раньше), один прогон на точку.
- В замере B: `Position += Velocity × dt` каждый кадр, модуль скорости `0.25–1.0` world units/s,
  координаты заворачиваются на `±5` (тор).

## Замер A: статичные квады

| Частиц | Кадр, мс | FPS | Draw calls (все / instanced) | Треугольники | Буфер матриц | Массив частиц |
|---:|---:|---:|---:|---:|---:|---:|
| 100 | 3.77 | 265.6 | 2 / 1 | 201 | 6.4 КБ | 2.8 КБ |
| 1 000 | 4.26 | 235.0 | 3 / 2 | 2 001 | 64 КБ | 28 КБ |
| 10 000 | 6.23 | 160.6 | 21 / 20 | 20 001 | 640 КБ | 280 КБ |
| 100 000 | 31.72 | 31.5 | 197 / 196 | 200 001 | 6.4 МБ | 2.8 МБ |
| 1 000 000 | 270.34 | 3.7 | 1 958 / 1 957 | 2 000 001 | 64 МБ | 28 МБ |

Размеры: `QiParticleData` = 28 байт, `Matrix4x4` = 64 байта (`UnsafeUtility.SizeOf`).
Инстансов на батч — 511, поэтому число instanced-вызовов = `ceil(n / 511)`.

- **1 000 000 не крашится.** 3.7 fps, 270 мс на кадр, все инстансы реально уходят в отрисовку.
  CPU-буферы на 1M: 64 МБ матриц + 28 МБ частиц, OOM нет.
- После 100 000 время кадра растёт линейно по числу частиц, ~0.27 мкс на частицу. Обрывов нет.
- До 10 000 рост почти не заметен: 6.23 мс в редакторе при бюджете 16.7 мс.
- На 1 000 000 `setPass` вырос с 2 до 9. Причина не выяснялась; на целевых числах не проявляется.
- Экран на 10 000+ полностью белый: все частицы в одном кольце, квады 9×9 px, overdraw
  максимальный. Это худший случай по заполнению.

## Замер B: с движением (S1)

| Частиц | Кадр, мс | FPS | Draw calls (все / instanced) | Треугольники |
|---:|---:|---:|---:|---:|
| 100 | 3.28 | 304.8 | 2 / 1 | 201 |
| 1 000 | 3.94 | 254.1 | 3 / 2 | 2 001 |
| 10 000 | 7.44 | 134.4 | 21 / 20 | 20 001 |
| 100 000 | 42.05 | 23.8 | 198 / 196 | 200 003 |
| 1 000 000 | 362.60 | 2.8 | 1 958 / 1 957 | 2 000 001 |

На целевом числе — **10 000: 7.44 мс / 134 fps**, запас до 60 fps (16.7 мс) примерно 2.2×.

- На 1 000 разница со статичным прогоном в пределах шума (3.94 против 4.26 мс).
- На 10 000 движение добавляет ~1.2 мс; на 100 000 — ~10 мс; на 1 000 000 — ~92 мс.
  Это около `0.1 мкс` на частицу за интеграцию и wrap, то есть цена линейная и предсказуемая.
- Сравнивать A и B напрямую как «цену `Tick`» нельзя: за 1.5 с прогрева кольцо распадается,
  и overdraw к моменту замера в B ниже, чем в A. Разница A→B — это `Tick` минус часть
  сэкономленной заливки, а не чистый `Tick`. Точную цену даст только `ProfilerMarker`,
  если она понадобится.

## Ограничения замера

- Это редактор, а не билд: абсолютные числа пессимистичны, важна форма кривой.
- Не разделены CPU и GPU. Внутри `Render()` нет маркеров, поэтому «линейно» — сумма сборки
  матриц на главном потоке, вызовов отрисовки и заливки.
- Один прогон на точку. 10 000 замерено дважды: 7.04 и 7.44 мс, разброс ~6%. Различия
  меньше этого порога не считать значимыми.
- Треугольники берутся из `UnityStats` и включают служебную геометрию редактора, поэтому
  возможны отклонения на 1–3 треугольника от `2 × частиц`.

## Целевое число

Цель — **10 000 частиц**. В редакторе с движением это 7.44 мс, запас до 60 fps ~2.2×.
Если на 10 000 кадр упадёт ниже 60 fps, значит регрессия в симуляции или в способе отрисовки,
а не «так и должно быть».

## Как повторить

1. Выставить `QiParticleManager._particleCapacity` в нужное число (инспектор или
   `SerializedObject`), войти в Play Mode.
2. Дождаться инициализации, запустить замер из editor-скрипта:

```csharp
using System;
using UnityEditor;
using UnityEngine;
using Unity.AI.Assistant.Agent.Dynamic.Extension.Editor;

internal class CommandScript : IRunCommand
{
    public void Execute(ExecutionResult result)
    {
        Application.runInBackground = true;

        double warmupEnd = Time.realtimeSinceStartupAsDouble + 1.5;
        bool warmedUp = false;
        double measureStart = 0;
        int measureStartFrame = 0;

        void Sample()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (!warmedUp)
            {
                if (now < warmupEnd) return;
                warmedUp = true;
                measureStart = now;
                measureStartFrame = Time.frameCount;
                return;
            }

            int frames = Time.frameCount - measureStartFrame;
            double elapsed = now - measureStart;
            if (frames < 60 && elapsed < 2.5) return;

            EditorApplication.update -= Sample;
            QiParticleManager manager = Object.FindAnyObjectByType<QiParticleManager>();
            Debug.Log(string.Format(
                "[Bench] capacity={0} frames={1} elapsed={2:F2}s avg={3:F2}ms fps={4:F1} draws={5} inst={6} tris={7}",
                manager.Capacity, frames, elapsed, elapsed / Math.Max(frames, 1) * 1000.0, frames / elapsed,
                UnityStats.drawCalls, UnityStats.standardInstancedInstances, UnityStats.triangles));
            EditorApplication.isPlaying = false;
        }

        EditorApplication.update += Sample;
        result.Log("Sampler started.");
    }
}
```

3. После прогона вернуть `_particleCapacity = 100` и сохранить сцену.
