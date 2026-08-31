Li o [SPIDER.md](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/SPIDER.md). Ele não é uma especificação nova; é a análise anterior copiada para um arquivo. Comparando com o código atual, eu priorizaria o seguinte:

### 1. Corrigir primeiro o cálculo das normais

Em [SceneManager.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Scene/SceneManager.cs:468), o método `FindClosestTriangleNormal` retorna a primeira face intersectada, não necessariamente a face atingida pelo raycast.

Isso pode causar:

- aranha inclinando para o lado errado;
- pernas mudando de direção;
- contatos instáveis em quinas;
- clipping em paredes;
- transições incorretas entre chão e parede.

Essa é a correção com melhor relação entre impacto e risco.

### 2. Adicionar offset físico nas patas

Em [ProceduralSpiderWalk.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Animation/ProceduralSpiderWalk.cs:349), o pé recebe diretamente o ponto da superfície.

O correto seria usar:

```text
posição do pé = ponto da superfície + normal * raio_da_pata
```

Atualmente o centro da pata é colocado exatamente na parede ou no chão, fazendo parte da geometria penetrar na superfície.

Essa alteração provavelmente já produziria uma melhora visual imediata.

### 3. Colocar colisão na trajetória do passo

Em [ProceduralSpiderWalk.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Animation/ProceduralSpiderWalk.cs:430), o passo é apenas uma interpolação com arco. O pé pode atravessar uma parede entre o ponto inicial e o ponto final.

O próximo upgrade deveria ser:

- testar a trajetória com uma esfera ou cápsula;
- verificar também a região próxima ao joelho;
- reduzir ou desviar o passo quando houver obstáculo;
- cancelar o passo se não existir espaço suficiente.

Isso resolveria o clipping que acontece durante o movimento, não apenas quando a pata pousa.

### 4. Estabilizar a escolha das superfícies

O sistema faz vários raycasts em [SpiderSurfaceSolver.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderSurfaceSolver.cs:435), mas pode trocar rapidamente entre chão, parede e faces vizinhas.

Seria importante adicionar:

- média das normais de vários contatos;
- preferência pelo contato atual;
- tolerância mínima antes de trocar de superfície;
- confirmação por alguns frames;
- rejeição de contatos muito diferentes da normal atual.

Isso reduziria tremores e mudanças repentinas nas pernas.

### 5. Depois, substituir o IK de dois ossos

O IK atual em [ProceduralSpiderWalk.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Animation/ProceduralSpiderWalk.cs:479) não resolve corretamente toda a cadeia.

Ele não usa integralmente:

- coxa;
- perna;
- tornozelo;
- ponta da pata.

O ideal seria um IK de três segmentos com:

- limites de rotação;
- pole vector por perna;
- orientação da pata alinhada à superfície;
- preservação do comprimento dos ossos;
- prevenção de dobras impossíveis.

Mas eu não começaria por ele. Se as normais e os contatos estiverem errados, um IK mais avançado apenas esconderá o problema e poderá criar novas deformações.

### 6. Calibrar o tamanho físico

Existem valores fixos espalhados pelo sistema:

- cápsula do corpo em [SpiderEnemy.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderEnemy.cs:177);
- escala do modelo `10.0f` em [SpiderEnemy.cs](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/src/Enemy/SpiderEnemy.cs:212);
- altura dos probes;
- distância dos probes;
- altura mínima e máxima do passo.

Esses valores deveriam ser reunidos em um perfil da aranha e calculados a partir do modelo. Isso evitaria o corpo físico ficar menor ou maior que o corpo visual.

### Ordem que eu implementaria agora

1. Corrigir `FindClosestTriangleNormal`.
2. Adicionar offset da pata pela normal.
3. Adicionar teste de colisão na trajetória do passo.
4. Estabilizar troca de contatos.
5. Calibrar dimensões do corpo e das pernas.
6. Só então implementar o IK completo de três segmentos.
7. Depois trabalhar na navegação e nos estados avançados da IA.

Eu deixaria para depois:

- oito pernas como corpos físicos dinâmicos;
- grafo completo de navegação;
- ragdoll totalmente autocolidível;
- sistema complexo de inteligência.

A melhor primeira etapa é corrigir as normais, o offset das patas e a colisão da trajetória. Esses três itens devem gerar uma melhora visual perceptível sem colocar em risco toda a locomoção atual. Não editei nenhum arquivo.