Perfeito, tenho toda a informação necessária. O modelo da aranha já tem **83 ossos** com uma hierarquia bem definida e **IK targets** para cada perna. Aqui está o plano:

---

## Plano: Aranha com Walk Procedural

### O que já temos no modelo

```
master.M_81 (root)
  └── IKMove.M_72
        └── cephalothorax.M_71 (corpo principal)
              ├── abdomen.M_2 (abdômen)
              ├── LegI_coxa.L_23 → trochanter → femur → patella → tibia → metatarsus → tarsus
              ├── LegII_coxa.L_16 → ...
              ├── LegIII_coxa.L_9 → ...
              ├── LegIV_coxa.L_30 → ...
              ├── LegI_coxa.R_37 → ...
              ├── LegII_coxa.R_44 → ...
              ├── LegIII_coxa.R_58 → ...
              ├── LegIV_coxa.R_51 → ...
              ├── palp_endite.L/R (pedipalpos)
              └── chelicera.L/R (quelíceras)
              
8 IK targets: legI_IKTarsus.L_73 ... legIV_IKTarsus.R_80
Animacao: "walk cycle" (já existe no FBX)
```

Cada perna tem **7 segmentos**: coxa → trochanter → femur → patella → tibia → metatarsus → tarsus

---

### Arquivos a criar/modificar

#### 1. `Fuse/src/Enemy/SpiderEnemy.cs` (NOVO)
Classe principal da aranha inimiga:
- Carrega o modelo `furrySpider.fbx`
- Resolve os índices dos 8 ossos de perna + palp + chelicera
- Cria `Animator` com o clip "walk cycle"
- Estado de patrulha (idle ↔ andando)
- A cada frame: roda o Animator, depois sobrescreve os ossos das pernas com IK procedural
- Struct `SpiderLeg` guardando: bone indices (coxa→tarsus), IK target index, fase (stance/swing), ângulo atual, timer

#### 2. `Fuse/src/Animation/ProceduralSpiderWalk.cs` (NOVO)
Sistema de walk procedural:
- Recebe o `Skeleton` + array de `SpiderLeg`
- `Update(float dt, float speed)`: para cada perna:
  - Calcula fase (stance = no chão, swing = no ar) baseado em ciclo temporal
  - **Stance**: perna se move para trás (simula contato com chão)
  - **Swing**: perna levanta (offset Y) e avança para a frente
- `ApplyIk(Skeleton skeleton)`: para cada perna, calcula rotações dos segmentos (coxa, femur, tibia) para atingir a posição alvo usando **Two-Bone IK** simplificado
- Grupo de pernas alternadas (I+III juntas, II+IV juntas — padrão aranha)

#### 3. `Fuse/src/Enemy/EnemySystem.cs` (MODIFICAR)
- Adicionar case para spawn de `SpiderEnemy` além do `Enemy` atual

#### 4. `Fuse/src/Core/Bible.cs` (MODIFICAR)
- Adicionar constante para o modelo da aranha:
```csharp
public const string SpiderModel = "skinned_models/furry_spider/furrySpider.fbx";
```

---

### Algoritmo do Walk Procedural

```
Para cada perna (8 total):
  1. Calcular fase no ciclo:
     cycleTime = (globalTime + phaseOffset[i]) % cycleDuration
     if cycleTime < stanceDuration → STANCE
     else → SWING

  2. STANCE (no chão):
     - Mover pé para trás (simula arrastar no chão)
     - Aplicar IK para atingir posição alvo
     - Manter pé no level do chão (raycast Y)

  3. SWING (no ar):
     - Levantar joelho (offset Y positivo)
     - Mover pé para frente
     - Usar interpolação cúbica para suavizar

  4. Aplicar rotações nos ossos via Skeleton.Nodes[idx].Local
```

**Padrão degaits da aranha:**
- **Walk**: I+III juntas, II+IV juntas (alternado)
- **Trot**: todas as pernas sincronizadas em pares diagonais

---

### Fluxo por frame

```
SpiderEnemy.Update(dt)
  ├─ EnemyPatrol.Update(dt) → move o corpo (physics)
  ├─ Animator.Update(dt) → roda "walk cycle" (corpo + abdômen)
  └─ ProceduralSpiderWalk.Update(dt, speed)
       ├─ Para cada perna: atualiza fase (stance/swing)
       ├─ Para cada perna: calcula posição alvo do pé
       ├─ Para cada perna: Two-Bone IK → rotações dos segmentos
       └─ Escreve em Skeleton.Nodes[idx].Local para cada osso de perna
```

---

### Próximos passos

1. **Descobrir os nomes exatos dos ossos** — O loader já loga os primeiros 10 nós. Precisamos logar todos para confirmar os nomes
2. **Criar `SpiderEnemy.cs`** — Carregar modelo, resolver bones, criar animator
3. **Criar `ProceduralSpiderWalk.cs`** — Lógica de IK e ciclo de pernas
4. **Testar** — Primeiro sem IK (só levantar/abaixar pernas), depois adicionar IK completo

Quer que eu comece preparando as regiões de código para o `SpiderEnemy.cs` e o `ProceduralSpiderWalk.cs`?