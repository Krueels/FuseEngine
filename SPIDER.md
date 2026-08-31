Sim. Analisei o sistema atual sem modificar nenhum arquivo.

A arquitetura atual é boa como base: o corpo usa `CharacterVirtual`, a aranha mantém contatos com superfícies e as pernas usam IK procedural. Porém, ela ainda não possui um sistema de IK realmente consciente de colisões.

O principal motivo das pernas atravessarem paredes é este:

- O raycast encontra apenas um ponto na superfície.
- Esse ponto é usado diretamente como alvo do pé.
- Não existe um raio de segurança para compensar o volume real do pé.
- A trajetória do pé durante o passo não verifica colisões.
- O IK ajusta os ossos sem testar se os segmentos atravessaram a geometria.

### Problemas críticos encontrados

1. O pé é colocado exatamente dentro da superfície

Em [ProceduralSpiderWalk.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Animation/ProceduralSpiderWalk.cs:349), o alvo recebe diretamente `landingSurface.Point`.

Esse ponto representa a superfície, não o centro físico do pé. Portanto, o pé deveria ser deslocado pela normal:

```text
posição do pé = ponto da superfície + normal * raio_do_pé
```

Sem isso, a ponta da perna inevitavelmente penetra pisos e paredes.

2. O passo não possui detecção de colisão

Em [ProceduralSpiderWalk.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Animation/ProceduralSpiderWalk.cs:430), a trajetória é apenas:

- interpolação linear entre início e fim;
- arco usando seno;
- deslocamento na normal da superfície.

Durante esse movimento não existe `ShapeCast`, `SphereCast` ou verificação dos segmentos da perna. Em um corredor estreito, o pé pode atravessar completamente uma parede mesmo que o destino esteja correto.

3. O IK atual não resolve a cadeia inteira

O método em [ProceduralSpiderWalk.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Animation/ProceduralSpiderWalk.cs:479) é descrito como um IK de dois ossos.

Ele:

- resolve principalmente quadril e joelho;
- usa `TipReach`, que é uma distância direta entre joelho e ponta;
- não respeita completamente `Length2` e `Length3`;
- reseta o tornozelo para a pose original;
- não alinha corretamente o pé com a normal da superfície;
- não possui limites físicos para joelho e tornozelo.

Isso pode gerar dobras artificiais e fazer partes intermediárias da perna atravessarem a parede.

4. O contato é baseado em raycasts de ponto

Em [SpiderSurfaceSolver.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderSurfaceSolver.cs:435), os pés são detectados através de vários raios.

Isso é melhor que um único raio, mas ainda não representa:

- largura do pé;
- espessura da perna;
- distância de segurança da parede;
- obstáculos entre o quadril e o pé.

O ideal seria usar uma combinação de:

- sphere cast para a ponta do pé;
- capsule cast para cada segmento da perna;
- teste de clearance ao redor do corpo;
- média das normais de vários contatos.

5. As normais das malhas podem estar erradas

Existe um problema importante em [SceneManager.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Scene/SceneManager.cs:468).

Em `FindClosestTriangleNormal`, o código retorna imediatamente a primeira face intersectada em [SceneManager.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Scene/SceneManager.cs:501).

A ordem dos triângulos da malha não garante que essa seja a face atingida pelo raycast. Em uma malha complexa, a aranha pode receber uma normal de outra face, provocando:

- corpo inclinando para o lado errado;
- pé escolhendo uma parede incorreta;
- transições instáveis;
- pernas entrando na geometria;
- mudanças repentinas de orientação.

Esse é provavelmente um dos problemas mais sérios do sistema atual.

6. O raycast aceita backfaces em praticamente tudo

Os probes utilizam `collideWithBackFaces: true` em [SpiderSurfaceSolver.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderSurfaceSolver.cs:524) e [SpiderSurfaceSolver.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderSurfaceSolver.cs:581).

Isso é útil para evitar problemas de winding, mas pode fazer a aranha considerar como suporte:

- o lado interno de uma parede;
- faces invertidas;
- triângulos internos;
- superfícies que deveriam ser ignoradas.

O ideal seria ter uma categoria específica de superfície escalável, por exemplo:

- `Climbable`;
- `Walkable`;
- `NonClimbable`;
- `OneWay`;
- `NoSpider`.

### Problemas de física

O corpo principal da aranha é uma cápsula fixa criada em [SpiderEnemy.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderEnemy.cs:177).

Mas o modelo usa escala fixa `10.0f` em [SpiderEnemy.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderEnemy.cs:212).

Isso significa que o tamanho físico do corpo não é calculado com base no modelo real. Se a cápsula for menor que o corpo visual, a aranha pode encostar na parede fisicamente enquanto o modelo e as pernas já estão dentro dela.

Também existe um problema em [SpiderEnemy.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderEnemy.cs:839): o dano aplica impulso ao `Body`, mas esse corpo é cinemático. Portanto, o impulso provavelmente não produz uma reação física real na aranha viva.

Para uma aranha convincente, seria melhor adicionar:

- reação visual ao impacto;
- recuo do corpo;
- perda temporária de aderência;
- cambaleio;
- animação de stagger;
- queda da parede quando o impacto for forte.

### Melhor solução para as pernas

Eu recomendo um sistema híbrido:

1. O corpo continua usando `CharacterVirtual`.

2. As pernas continuam sendo controladas por IK, evitando oito corpos dinâmicos instáveis.

3. Cada perna recebe uma representação física de consulta:

- cápsula para coxa;
- cápsula para tíbia;
- cápsula para tornozelo;
- esfera ou cápsula para a pata.

4. Depois do IK, o sistema testa cada segmento contra a geometria.

5. Se um segmento colidir:

- afasta o joelho da parede;
- reduz o alcance do pé;
- altera o pole vector;
- diminui a altura do passo;
- ou cancela o passo e escolhe outro ponto.

Esse modelo é muito mais estável que simular oito pernas rigidamente em tempo real.

### Melhorias de inteligência

O sistema de perseguição atual ainda é local. O planner testa algumas direções e escolhe uma transição próxima em [SpiderSurfacePursuitPlanner.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderSurfacePursuitPlanner.cs:361).

Isso pode falhar em:

- casas com vários cômodos;
- escadas;
- paredes paralelas;
- vãos;
- tetos baixos;
- objetos móveis;
- superfícies parcialmente bloqueadas.

Para evoluir a IA:

- criar uma malha de navegação para superfícies;
- transformar cada parede, chão e teto em patches navegáveis;
- conectar patches através de bordas;
- usar A* ou Dijkstra;
- armazenar custo de cada transição;
- considerar espaço necessário para o corpo;
- validar se existe espaço para todas as pernas;
- recalcular a rota quando o corpo ficar bloqueado.

Atualmente o alvo da perseguição também é global e estático em [SpiderPatrol.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderPatrol.cs:19). Isso funciona para uma única aranha, mas não escala para múltiplas aranhas com comportamentos diferentes.

Também não encontrei estados completos de:

- patrulha;
- alerta;
- investigação;
- perseguição;
- ataque;
- fuga;
- busca após perder o jogador.

### Melhorias de animação

O sistema procedural é atualizado dentro do fixed timestep em [Application.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Core/Application.cs:415).

Isso garante estabilidade física, mas pode causar:

- passos visualmente bruscos;
- pequenas travadas;
- pernas saltando entre posições quando o FPS é baixo;
- diferença entre pose física e pose renderizada.

O ideal seria manter duas poses:

- pose física atual;
- pose física anterior;

e interpolar a pose no render.

Também seria importante separar:

- animação base do corpo;
- animação de caminhada;
- IK das pernas;
- reação a impactos;
- animação de ataque;
- animação de escalada.

### Ragdoll

O ragdoll já possui partes físicas articuladas, mas a colisão entre partes está desativada por padrão em [SpiderRagdollDefinition.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderRagdollDefinition.cs:48) e [SpiderRagdollDefinition.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderRagdollDefinition.cs:77).

Para evitar instabilidade, não recomendo ativar todas as colisões imediatamente. O melhor seria:

- ativar colisão apenas entre corpo e mundo;
- permitir algumas colisões entre pernas;
- usar grupos de colisão;
- aplicar limites de junta;
- aumentar amortecimento;
- reduzir massa das extremidades;
- limitar a velocidade angular.

### Ordem recomendada

A sequência mais segura seria:

1. Corrigir o cálculo da normal em `FindClosestTriangleNormal`.

2. Adicionar offset do pé baseado na normal e no raio físico da pata.

3. Criar sphere/capsule casts para detectar obstáculos.

4. Substituir o IK atual por um IK de três segmentos com limites de junta.

5. Validar colisão dos segmentos da perna depois do IK.

6. Criar trajetórias de passo que desviem de paredes e tetos.

7. Criar uma camada de superfícies escaláveis.

8. Calibrar automaticamente corpo, pernas, pés e clearance a partir do modelo.

9. Adicionar navegação por grafo de superfícies.

10. Adicionar interpolação visual entre os passos do fixed timestep.

O ponto mais urgente é a combinação de:

- normal possivelmente incorreta;
- alvo do pé sem offset;
- ausência de colisão durante o arco;
- IK sem restrições de volume.

Corrigindo esses quatro itens, a maior parte do clipping visual deve desaparecer sem precisar transformar as pernas em corpos físicos dinâmicos. Não editei nenhum arquivo nesta análise.