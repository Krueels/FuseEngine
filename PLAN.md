# PLAN: Separação de Sistemas Gameplay vs FPS + Weapon System (Glock)

## Problema Atual

O conflito está em **`PickupController.cs:60-61`**:
```csharp
if (_heldBodyID.IsValid && Input.Input.LeftMousePressed())
    DropObject(true);  // Arremessa objeto com botão esquerdo
```

O sistema de tiro da Glock também precisará do **botão esquerdo do mouse** → **conflito direto**.

---

### Arquitetura Atual de Input

```
Input.cs (static) → Polling direto GLFW
    ↓
PickupController.Update() → Verifica Input.Input.LeftMousePressed() diretamente
    ↓
Application.HandleInput() → Verifica teclas globais (F1, F2, F5, F9, `, Insert, G)
    ↓
Player.Update() → Verifica Input.Input.KeyDown/KeyPressed para movimento
```

**Problemas:**
1. **Acoplamento forte**: Cada sistema lê `Input.Input` diretamente
2. **Sem prioridade**: Não há mediação de quem "ganha" o clique
3. **Difícil testar/estender**: Adicionar arma = modificar múltiplos arquivos

---

## Solução Proposta: Sistema de Input Contextual + Weapon System

#### 1. Novo Arquivo: `Fuse/src/Input/InputContext.cs`
```csharp
// Define contextos de input mutuamente exclusivos
public enum InputContext
{
    Gameplay,      // Movimento, interação, pickup
    Weapon,        // Atirar, recarregar, trocar arma
    UI,            // Menus, console, editor
    Noclip         // Voo livre
}

// Gerenciador de contexto ativo + prioridades
public static class InputManager
{
    public static InputContext CurrentContext { get; private set; }
    public static bool IsContextActive(InputContext ctx) => CurrentContext == ctx;
    
    // Request context change with priority
    public static bool RequestContext(InputContext newCtx, int priority = 0) { ... }
    public static void ReleaseContext(InputContext ctx) { ... }
}
```

#### 2. Novo Arquivo: `Fuse/src/Player/WeaponSystem.cs`
```csharp
// Sistema centralizado de armas
public class WeaponSystem : IDisposable
{
    private readonly Player.Player _player;
    private readonly Camera _camera;
    private readonly PhysicsWorld _physics;
    private readonly AssetManager _assets;
    private readonly AudioSystem _audio;
    
    // Estado atual
    private IWeapon _currentWeapon;
    private readonly Dictionary<string, IWeapon> _weapons = [];
    
    // Viewmodel entity reference
    private Entity _viewmodelEntity;
    private Animator _viewmodelAnimator;
    
    public void Update(float dt) { ... }
    public void PhysicsUpdate(float dt) { ... }
    public bool TryShoot() { ... }
    public void SwitchWeapon(string weaponId) { ... }
}
```

#### 3. Nova Interface: `Fuse/src/Player/IWeapon.cs`
```csharp
public interface IWeapon : IDisposable
{
    string Id { get; }
    string ViewmodelModelPath { get; }  // ex: "skinned_models/Glock.fbx"
    string ViewmodelIdleAnim { get; }   // ex: "Idle"
    string ViewmodelFireAnim { get; }   // ex: "Fire"
    string ViewmodelReloadAnim { get; } // ex: "Reload"
    
    float FireRate { get; }             // tiros/segundo
    float Damage { get; }
    float Range { get; }
    int MagazineSize { get; }
    int ReserveAmmo { get; set; }
    
    void OnEquip(WeaponSystem system);
    void OnUnequip();
    bool CanFire();
    void Fire(Vector3 origin, Vector3 direction);
    void Reload();
    void Update(float dt);
    void UpdateViewmodel(float dt, Animator animator);
}
```

#### 4. Implementação Concreta: `Fuse/src/Player/Weapons/GlockWeapon.cs`
```csharp
public sealed class GlockWeapon : IWeapon
{
    public string Id => "glock";
    public string ViewmodelModelPath => "skinned_models/Glock.fbx";
    public string ViewmodelIdleAnim => "Idle";
    public string ViewmodelFireAnim => "Fire";
    public string ViewmodelReloadAnim => "Reload";
    
    public float FireRate => 12.0f;  // 720 RPM
    public float Damage => 25f;
    public float Range => 100f;
    public int MagazineSize => 17;
    public int ReserveAmmo { get; set; } = 120;
    
    private float _nextFireTime;
    private int _currentAmmo;
    private bool _isReloading;
    private float _reloadTimer;
    
    // Raycast shooting + hit effects + animation trigger
    public void Fire(Vector3 origin, Vector3 direction) { ... }
}
```

#### 5. Refatorar: `PickupController.cs` → Usar InputContext
```csharp
public void Update(float dt)
{
    // Só processa pickup se NÃO estiver em contexto de arma
    if (InputManager.IsContextActive(InputContext.Weapon)) return;
    
    if (Input.Input.KeyPressed(KeyCodes.E)) { ... }
    if (_heldBodyID.IsValid && Input.Input.LeftMousePressed()) { ... }
}
```

#### 6. Refatorar: `Application.cs` → Inicializar WeaponSystem
```csharp
// Em Application.Init():
_weaponSystem = new WeaponSystem(_player, _player.Camera, _physics, _assets, _audio);
_weaponSystem.RegisterWeapon(new GlockWeapon());
_weaponSystem.Equip("glock");  // Spawna viewmodel, configura animator

// Em Application.Run() loop:
if (!_paused)
{
    _weaponSystem.Update(dt);
    _weaponSystem.PhysicsUpdate(dt);
}

// Em HandleInput():
if (InputManager.IsContextActive(InputContext.Weapon))
{
    if (Input.Input.LeftMousePressed() || Input.Input.LeftMouseDown())
        _weaponSystem.TryShoot();  // Auto/semi-auto baseado na arma
    
    if (Input.Input.KeyPressed(KeyCodes.R))
        _weaponSystem.Reload();
    
    if (Input.Input.KeyPressed(KeyCodes.Alpha1))
        _weaponSystem.SwitchWeapon("glock");
}
```

---

### Estrutura de Arquivos Proposta

```
Fuse/src/
├── Input/
│   ├── Input.cs                 # (existente) Polling baixo nível
│   ├── InputContext.cs          # (NOVO) Contextos + prioridades
│   └── InputManager.cs          # (NOVO) Mediação de contexto
│
├── Player/
│   ├── Player.cs                # (existente) Movimento, noclip, câmera
│   ├── PickupController.cs      # (REFATORAR) Usar InputContext
│   ├── WeaponSystem.cs          # (NOVO) Gerenciador central de armas
│   ├── IWeapon.cs               # (NOVO) Interface de arma
│   └── Weapons/
│       ├── GlockWeapon.cs       # (NOVO) Implementação da Glock
│       ├── WeaponBase.cs        # (NOVO) Base abstrata opcional
│       └── Projectile/          # (FUTURO) Balas, hitscan, tracers
│           ├── HitscanWeapon.cs
│           └── ProjectileWeapon.cs
│
└── Interaction/                 # (existente) Sistema de interação
    ├── InteractionSystem.cs
    ├── IInteractable.cs
    └── InteractableTypeAttribute.cs
```

---

### Fluxo de Prioridade de Input

```
Prioridade 1 (Maior): UI/Console/Menu → InputContext.UI
Prioridade 2:       Weapon (atirar, recarregar) → InputContext.Weapon  
Prioridade 3:       Gameplay (pickup E, throw LMB) → InputContext.Gameplay
Prioridade 4:       Movement (WASD, Space, Shift, Ctrl) → Sempre ativo
Prioridade 5:       Debug (F1, F2, F5, F9, G) → Sempre ativo
Prioridade 6 (Menor): Noclip → InputContext.Noclip
```

**Regras:**
- `Weapon` **bloqueia** `Gameplay` para LMB
- `UI` **bloqueia** tudo exceto `Debug`
- `Noclip` **substitui** `Movement` mas mantém `Weapon`/`Gameplay` disponíveis
- Troca de arma (1,2,3...) sempre funciona

---

### Próximos Passos Recomendados (Ordem)

1. **Criar `InputContext.cs` + `InputManager.cs`** - Base da separação
2. **Criar `IWeapon.cs` + `WeaponSystem.cs`** - Core do sistema
3. **Criar `GlockWeapon.cs`** - Implementação concreta com:
   - Raycast hitscan
   - Muzzle flash (partícula/light temporária)
   - Trigger animação "Fire" no viewmodel
   - Som de tiro
   - Casquinha (opcional, particle system)
4. **Refatorar `PickupController.cs`** - Verificar `InputManager.IsContextActive(InputContext.Weapon)`
5. **Integrar em `Application.cs`** - Instanciar, registrar, chamar Update
6. **Adicionar animações no Glock.fbx** - Fire, Reload, Draw, Holster

---

### Benefícios Dessa Arquitetura

| Aspecto | Antes | Depois |
|---------|-------|--------|
| **Adicionar arma** | Modificar 3+ arquivos | 1 arquivo (`NovaArmaWeapon.cs`) |
| **Testar arma** | Precisa rodar jogo | `WeaponSystem` testável isolado |
| **Trocar arma** | Não existe | `SwitchWeapon("id")` |
| **Prioridade input** | Conflito LMB | Explícito via `InputContext` |
| **Viewmodel** | Hardcoded no Application | Definido na `IWeapon` |
| **Animações** | Manual no Application | Automático via `UpdateViewmodel()` |

---

### Notas Importantes

1. **Viewmodel atual**: O `SpawnGlockViewModel()` em `Application.cs` deve mover para `WeaponSystem.Equip()` - cada arma gerencia seu próprio viewmodel
2. **Animações**: O `Glock.fbx` precisa ter clips nomeados: `Idle`, `Fire`, `Reload`, `Draw`, `Holster`
3. **Muzzle Flash**: Pode ser um `Light` spot temporária + particle system (futuro)
4. **Hit Effects**: Decal de impacto, som, particle - delegar para `WeaponSystem.SpawnHitEffect()`
5. **Network/MP futuro**: `WeaponSystem` isolado facilita sincronização estado (ammo, fire timer)

---

### Resumo: O Que Criar (Arquivos Novos)

| Arquivo | Responsabilidade |
|---------|------------------|
| `InputContext.cs` | Enum + struct de prioridade |
| `InputManager.cs` | Stack de contextos ativos, mediação |
| `IWeapon.cs` | Contrato de arma (dados + comportamento) |
| `WeaponSystem.cs` | Gerenciador: equip, update, shoot, reload, switch |
| `Weapons/GlockWeapon.cs` | Lógica específica da Glock (raycast, ammo, anims) |
| `Weapons/WeaponBase.cs` (opcional) | Base compartilhada: cooldown, ammo, viewmodel helper |

### O Que Refatorar

| Arquivo | Mudança |
|---------|---------|
| `PickupController.cs` | `if (InputManager.IsContextActive(InputContext.Weapon)) return;` no topo do `Update` |
| `Application.cs` | Remover `SpawnGlockViewModel`, `UpdateViewmodelTransform`; adicionar `_weaponSystem` init + update |