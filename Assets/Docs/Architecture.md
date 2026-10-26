# Архитектура прототипа

Статус: ранний прототип. Документ фиксирует правила композиции, а не финальную систему.
Обновлять при каждом изменении состава сервисов или порядка кадра.

## 1. Единая точка входа

`Bootstrap` — единственный MonoBehaviour, у которого есть `Awake` и `Update`.
Он владеет порядком инициализации и порядком кадровых вызовов.

```csharp
public sealed class Bootstrap : MonoBehaviour
{
    [SerializeField] QiParticleManager _particleManager;
    [SerializeField] QiParticleRenderer _particleRenderer;

    void Awake()
    {
        Initialize();
    }

    void Update()
    {
        _particleRenderer.Render();
    }

    void Initialize()
    {
        _particleManager.Initialize();
        _particleRenderer.Initialize(_particleManager);

        Debug.Log("[Bootstrap] Application initialized.");
    }
}
```

Сцена — composition root. Все ссылки между сервисами выставляются в инспекторе `Bootstrap`
и больше нигде. В коде нет `GetComponent`, `Find*`, статических синглтонов и сервис-локаторов.

Проводка намеренно не валидируется: если ссылка не проставлена, будет обычный
`NullReferenceException` при первом обращении. Это быстрее, чем стена проверок, которая
маскирует поломку.

## 2. Сервисы

Сервис — постоянный объект, живущий всю жизнь сцены, в единственном экземпляре.

- Отдельный GameObject в корне сцены, имя совпадает с именем скрипта (`QiParticleRenderer`).
  Не вешать несколько сервисов на один объект: в инспекторе их потом не найти, а имя объекта
  начинает противоречить содержимому.
- Зависимости приходят аргументами в `Initialize(...)`. Не через поля инспектора и не через
  `GetComponent`.
- Сервис не имеет `Awake`, `Start`, `Update`, `OnEnable`. Единственное исключение —
  `OnDestroy` для освобождения нативных ресурсов (`NativeArray`); он должен быть идемпотентным.
- Данные принадлежат одному владельцу. `QiParticleManager` владеет частицами и полем,
  `QiParticleRenderer` — только мешем, материалом и буфером матриц. Рендерер не хранит
  состояние симуляции, менеджер ничего не знает про отрисовку.

```csharp
public void Initialize(QiParticleManager particleManager)
{
    _particleManager = particleManager;
    // ...
}
```

## 3. Порядок кадра

Все кадровые вызовы идут из `Bootstrap.Update` в явном порядке:

```
Bootstrap.Update
├─ QiParticleManager.Tick(dt)   // появится вместе с движением частиц
└─ QiParticleRenderer.Render()
```

Почему так, а не `Update` в каждом сервисе:

- порядок «симуляция → рендер» виден в одном месте и не зависит от порядка загрузки скриптов;
- один `Update` вместо N: меньше накладных расходов и одна точка для `ProfilerMarker`;
- можно поставить breakpoint, выполнить кадр вручную, отключить отдельную стадию;
- `Script Execution Order` не используем: он живёт в настройках проекта, не виден в коде,
  действует на все сцены и легко забывается.

Ограничение подхода: он ручной. Пока сервисов меньше пяти и у них одна частота, это дешевле
любой инфраструктуры. Реестр `ITickable`, `ServiceLocator` или кастомный `PlayerLoop` вводим
только когда появятся разные частоты (fixed step), десятки систем или профилирование покажет,
что один `Update` стал узким местом.

## 4. Текущий состав

| Объект | Компонент | Ответственность | Зависимости |
|---|---|---|---|
| `Bootstrap` | `Bootstrap` | порядок инициализации и кадра | `QiParticleManager`, `QiParticleRenderer` |
| `QiParticleManager` | `QiParticleManager` | поле `QiFieldGrid` 1000×1000, `NativeArray<QiParticleData>` на 100 частиц, спавн, `ActiveCount` | — |
| `QiParticleRenderer` | `QiParticleRenderer` | инстансированная отрисовка частиц одним draw call | `QiParticleManager` через `Initialize` |

Рантайм-поток:

1. `Bootstrap.Awake` → `Initialize`.
2. `QiParticleManager.Initialize` выделяет поле и массив частиц, спавнит 100 штук.
3. `QiParticleRenderer.Initialize` берёт меш и материал из префаба `QiParticleQuad`,
   выделяет буфер матриц и `RenderParams`.
4. `Bootstrap.Update` → `Render()`: матрицы `TRS` из позиций, один
   `Graphics.RenderMeshInstanced`.

Префаб не инстанцируется. Он используется как носитель меша и материала, поэтому в сцене нет
100 GameObject с Transform, а все частицы рисуются одним вызовом.

## 5. Мир и координаты

- Поле: `10 × 10` world units, сетка `1000 × 1000`, клетка `0.01` (`QiFieldGrid`).
- Камера: orthographic, size `5`, позиция `(0, 0, -10)`, фон чёрный.
- Частица: квадрат `0.15` world units, белый unlit, GPU instancing включён.

Константы мира меняются только в `QiFieldGrid`, не дублируются по скриптам.

## 6. Известные ограничения

- `QiParticleRenderer` выделяет буфер матриц ровно под `ParticleCapacity` (100). При росте
  количества частиц нужно менять capacity и разбивать вызовы на батчи.
- `Graphics.RenderMeshInstanced` принимает максимум 1023 инстанса за вызов, а при наличии
  `worldToObject` в инстанс-данных — 511. URP Unlit не использует
  `#pragma instancing_options assumeuniformscaling`, поэтому текущий практический предел —
  511 инстансов на вызов.
- `RenderParams.worldBounds` задан как `10 × 10`: весь батч отсекается и сортируется как одна
  сущность. Частицы за пределами bounds исчезнут.
- Матрицы собираются на главном потоке. На 100 частицах это незаметно, на десятках тысяч —
  главный кандидат на перенос в jobs/Burst.
- Одна сцена, загрузка сцен не используется. `DontDestroyOnLoad` не применяется: сервисы
  принадлежат сцене, при её смене всё пересоздаётся.

## 7. Соглашения по коду

- Проект маленький, поэтому явные типы вместо `var`: читаемость важнее компактности.
- Приватные поля — `_camelCase`, один публичный класс на файл, `sealed` там, где не нужно
  наследование.
- Публичные методы сервиса — только те, которые вызывает `Bootstrap` (плюс геттеры данных).
- `NativeArray` и прочие нативные ресурсы освобождаются в `OnDestroy` сервиса-владельца.

## 8. Чеклист для нового сервиса

1. Отдельный файл `Assets/Scripts/<Name>.cs`, класс `sealed`, `MonoBehaviour` только если нужен
   GameObject.
2. Отдельный GameObject в сцене с именем `<Name>`.
3. Поле `[SerializeField] <Name> _<name>;` в `Bootstrap` и ссылка в инспекторе.
4. Публичный `Initialize(...)` с зависимостями-аргументами.
5. Вызов из `Bootstrap.Initialize` в правильном порядке.
6. Если нужен кадровый вызов — публичный метод, вызываемый из `Bootstrap.Update`.
7. Никаких `Awake`/`Start`/`Update` внутри сервиса, никаких `GetComponent` и `Find*`.

## 9. Открытые вопросы

- Движение частиц: добавить `QiParticleManager.Tick(dt)` и вызвать его в `Bootstrap.Update`
  перед `Render()`.
- Масштабирование: capacity, батчи по 511 и замер на 100 / 1 000 / 10 000 частиц.
- Перенос сборки матриц в jobs/Burst — только после появления реального дрейфа.
- Нужен ли префаб как носитель меша, или достаточно прямых ссылок на `Mesh` и `Material`.
