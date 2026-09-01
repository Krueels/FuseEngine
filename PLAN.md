Sim. A análise mostra que nossa implementação já possui o esqueleto correto, mas ainda falta trocar o modelo de densidade e iluminação. O realismo não virá de simplesmente aumentar samples ou render scale.

Não alterei nenhum arquivo nesta etapa.

## Conclusão principal

Nossa nuvem atualmente é formada por:

- uma textura 3D Perlin-Worley;
- uma textura 3D de detalhe;
- um weather map;
- um perfil vertical simples;
- raymarch de 64 passos;
- cone lighting;
- reprojeção temporal.

Isso é suficiente para gerar nuvens, mas não para produzir nuvens realistas. O problema principal está em como esses dados são combinados.

A Unity HDRP separa claramente:

1. distribuição horizontal da nuvem;
2. tipo da nuvem;
3. perfil vertical;
4. forma macro;
5. erosão das bordas;
6. absorção da luz;
7. iluminação ambiente;
8. espalhamento múltiplo.

Nossa shader mistura quase tudo em uma única função `SampleCloudDensity`.

## Como a Unity HDRP organiza o sistema

O caminho do editor que você enviou controla a criação dos dados, mas o algoritmo principal está no runtime do HDRP, especialmente em `VolumetricCloudsUtilities.hlsl`.

Fontes oficiais:

- [Editor de Volumetric Clouds da Unity](https://github.com/Unity-Technologies/Graphics/tree/master/Packages/com.unity.render-pipelines.high-definition/Editor/Lighting/VolumetricClouds)
- [Runtime de Volumetric Clouds da Unity](https://github.com/Unity-Technologies/Graphics/tree/master/Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/VolumetricClouds)
- [Documentação oficial do HDRP Volumetric Clouds](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.high-definition/Documentation~/volumetric-clouds-volume-override-reference.md)

A Unity utiliza dois mapas principais.

### Cloud Map

Cada canal possui uma função diferente:

- R: cobertura;
- G: quantidade de chuva e escurecimento;
- B: tipo da nuvem;
- A: altura máxima da nuvem em determinadas regiões.

### Cloud LUT

A LUT representa o comportamento vertical de cada tipo de nuvem:

- R: densidade ao longo da altura;
- G: quantidade de shaping e erosão;
- B: ambient occlusion.

A posição horizontal da LUT representa o tipo da nuvem. A posição vertical representa a altura dentro da nuvem.

Isso permite que uma área tenha nuvens stratus e outra área tenha cumulus sem trocar o shader inteiro.

## Como a densidade deveria ser calculada

Conceitualmente:

```glsl
height = altura_normalizada_dentro_da_nuvem;

weather = sample_cloud_map(world_position.xz);

profile = sample_lut(weather.cloud_type, height);

macro_noise = sample_perlin_worley(world_position);

detail_noise = sample_erosion_noise(world_position);

base_shape =
    aplicar_cobertura(
        aplicar_perfil_vertical(
            macro_noise,
            profile.density,
            weather.coverage));

base_shape =
    aplicar_erosao(
        base_shape,
        detail_noise,
        profile.erosion);

density = base_shape * density_multiplier;
```

A nossa shader possui quase todos os ingredientes, mas atualmente:

- o perfil vertical é calculado por três `smoothstep`;
- o weather map não possui semântica de chuva, altura máxima ou ambient occlusion;
- a erosão não é controlada por uma LUT;
- o preset altera globalmente a nuvem inteira;
- a variação entre tipos é pequena;
- o mesmo volume 3D é usado repetidamente.

Por isso o resultado tende a parecer uma grande camada deformada, em vez de várias formações diferentes.

## Como a iluminação volumétrica funciona

Para cada ponto dentro da nuvem, precisamos calcular:

```glsl
transmittance_view =
    exp(-densidade * absorcao * distancia);
```

Depois devemos calcular quanto da luz solar chega até aquele ponto:

```glsl
transmittance_light =
    exp(-integral_da_densidade_na_direcao_do_sol);
```

A contribuição final é aproximadamente:

```glsl
contribuicao =
    transmittance_view *
    (1.0 - transmittance_segmento) *
    luz_solar *
    phase_function *
    transmittance_light;
```

A Unity faz isso em `EvaluateCloudProperties`, `EvaluateSunTransmittance` e `EvaluateCloud`.

Ela também usa:

- função de fase Henyey-Greenstein;
- espalhamento para frente e para trás;
- powder effect;
- dois níveis de multi-scattering;
- ambient occlusion baseado na forma da nuvem;
- luz ambiente superior e inferior;
- absorção diferente para nuvens com chuva.

A fonte do algoritmo está no [runtime oficial de Volumetric Clouds da Unity](https://github.com/Unity-Technologies/Graphics/tree/master/Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/VolumetricClouds).

## O problema da nossa iluminação

Em [`volumetric_clouds.frag`](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/res/Shaders/Clouds/volumetric_clouds.frag), a iluminação ainda é bastante estilizada.

Há alguns pontos importantes:

```glsl
vec3 ambientLight = mix(ambientBottom, ambientTop, heightFraction);
```

Essas cores ambiente são fixas no shader.

Também temos:

```glsl
float silverLining = 0.14 + phase * 1.65;
```

Isso força uma borda iluminada artificialmente.

E:

```glsl
vec3 directLight = sunRadiance * sunHeight *
    lightVisibility * silverLining;
```

Essa iluminação não depende corretamente da quantidade de densidade acumulada ao redor do ponto. Ela cria uma aparência mais “cartoon”, com bordas muito claras e interiores sem profundidade.

O `powder effect` atual também é apenas baseado na extinção do segmento. Ele não possui o comportamento mais completo usado pela Unity.

## Comparação com Robert Beckebans

O repositório do Robert é uma referência muito boa para a parte visual e prática:

- [shader principal do Robert](https://github.com/RobertBeckebans/OpenGL-VolumetricClouds/blob/master/shaders/volumetric_clouds.frag)
- [gerador Perlin-Worley](https://github.com/RobertBeckebans/OpenGL-VolumetricClouds/blob/master/shaders/perlinworley.comp)
- [gerador de weather map](https://github.com/RobertBeckebans/OpenGL-VolumetricClouds/blob/master/shaders/weather.comp)
- [repositório completo](https://github.com/RobertBeckebans/OpenGL-VolumetricClouds)

Ele utiliza:

- volume Perlin-Worley 128³;
- ruído Worley de detalhe;
- weather map;
- 64 passos principais;
- seis amostras de luz;
- cone lighting;
- múltiplas funções de fase;
- atmosfera integrada;
- dithering Bayer;
- iluminação de frente e de trás.

Mas existe uma observação importante: o shader do Robert também possui partes estilizadas e simplificadas. Ele usa cores ambiente fixas, valores artísticos e até desativa parte do powder effect em alguns trechos.

O motivo de parecer muito melhor não é apenas o número de samples. É a combinação de:

- macroformas coerentes;
- erosão aplicada principalmente nas bordas;
- perfil vertical bem calibrado;
- iluminação atmosférica;
- contraste entre regiões densas e regiões vazias;
- escalas de ruído diferentes.

Devemos copiar os princípios, não o shader inteiro.

## Comparação com o projeto Godot

O projeto do Godot segue uma arquitetura mais moderna:

- [README do projeto Godot](https://github.com/clayjohn/godot-volumetric-cloud-demo-v2)
- [shader de nuvem do projeto](https://github.com/clayjohn/godot-volumetric-cloud-demo-v2/blob/main/cloud_sky/clouds.glsl)

Ele utiliza:

- compute shaders para gerar e atualizar texturas;
- raymarch de texturas 3D;
- atmosfera fisicamente mais consistente;
- atualização do hemisfério ao longo de 64 frames;
- duas texturas temporais interpoladas;
- atualização automática com base no sol;
- múltiplas amostras de iluminação.

O próprio projeto lista hierarchical raymarching como uma melhoria futura, portanto ele também não é uma solução perfeita. Entretanto, ele confirma uma coisa importante: a qualidade vem da coerência entre céu, sol, atmosfera e nuvem, não apenas do shader de nuvem isolado.

## O shader Sky++ não deve ser nossa base principal

O [Sky++](https://godotshaders.com/shader/sky-sorta/) tem boas ideias:

- nuvens cumulus raymarched;
- 64 marches;
- light marches separados;
- ruído 3D;
- jitter;
- renderização em meia ou quarta resolução;
- atmosfera e estrelas.

Mas o próprio autor classifica o projeto como WIP e “very unoptimized”. Ele é útil como referência visual e didática, mas não deve ser a arquitetura principal da Fuse.

## Diagnóstico da nossa implementação atual

### 1. O perfil de nuvem é simples demais

Em [`cloud_common.glsl`](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/res/Shaders/Clouds/cloud_common.glsl), `CloudHeightProfile` mistura três gradientes analíticos.

Isso é um bom começo, mas não permite controlar independentemente:

- base da nuvem;
- topo da nuvem;
- densidade do interior;
- erosão por altura;
- ambient occlusion por altura;
- forma específica de cada tipo.

A solução correta é criar uma LUT de nuvem.

### 2. O weather map ainda é apenas procedural básico

Em [`cloud_weather.comp`](C:/Users/niko/HDD2/DEV/Csharp/FuseEngine/Fuse/res/Shaders/Clouds/cloud_weather.comp), temos cobertura, tipo e variação.

Mas esses canais ainda não possuem a mesma função do HDRP. Falta:

- rain mask;
- max cloud height;
- controle de densidade;
- regiões de transição;
- controle de forma por tipo.

### 3. A erosão precisa atuar na borda

A erosão realista não deve destruir a nuvem inteira de forma uniforme.

Ela deve:

- preservar o núcleo da nuvem;
- cortar principalmente as bordas;
- gerar wisps na parte inferior;
- ser mais fraca no centro;
- variar conforme o tipo da nuvem;
- usar escalas de ruído diferentes.

Atualmente, a erosão ainda é muito próxima de uma subtração global.

### 4. O volume 3D ainda repete

O volume 128³ é periódico. Isso é normal, mas uma única textura repetida sempre revelará seus padrões.

O nosso código tenta separar o período do weather map usando escalas diferentes, mas isso apenas desloca a repetição. Não elimina a periodicidade.

Para esconder a repetição, precisamos combinar:

- base noise em escala grande;
- segunda base noise rotacionada;
- detail noise em escala média;
- micro erosion;
- domain warping;
- offsets independentes;
- velocidades diferentes;
- possibilidade de misturar dois volumes com sementes diferentes.

### 5. A iluminação solar ainda não é fisicamente consistente

O atual `LightTransmittance` usa uma distância aproximada baseada em:

```glsl
uCloudThickness / abs(uSunDirection.y)
```

Isso não representa corretamente a distância real até o limite da casca esférica.

O cálculo deveria encontrar a interseção do raio solar com o limite superior da camada e marchar somente dentro do volume.

Os seis light steps podem continuar existindo. O importante é que eles sejam aplicados sobre o intervalo correto e usando uma versão barata da densidade.

### 6. Falta multi-scattering real

Sem multi-scattering, o interior da nuvem fica excessivamente preto ou chapado.

A Unity usa dois níveis adicionais aproximados de espalhamento. Isso suaviza o interior sem simplesmente aumentar a luz ambiente.

Esse é um dos principais motivos pelos quais o HDRP mantém volume mesmo em áreas densas.

### 7. O ambiente está desacoplado do céu

As cores ambiente da nossa nuvem são fixas. O ideal é que elas venham de:

- sky LUT;
- cor do céu;
- luz ambiente superior;
- luz ambiente inferior;
- cor do terreno;
- intensidade solar atual.

O projeto Godot também utiliza uma sky LUT para alimentar a iluminação das nuvens. Isso é uma parte importante do resultado visual.

### 8. A reprojeção temporal ainda é simples

A nossa reprojeção utiliza um ponto representativo e uma região de clamp fixa.

A Unity usa:

- profundidade da nuvem;
- profundidade da cena;
- análise de vizinhança;
- clipping da história;
- validação de reprojeção;
- rejeição quando o sol muda;
- rejeição quando a nuvem se desloca;
- filtros de redução de ghosting.

A diferença aparece como:

- pontilhado;
- manchas temporárias;
- bordas instáveis;
- nuvens “derretendo” quando a câmera se move.

Isso deve ser corrigido depois da densidade e da iluminação, porque um denoiser não consegue corrigir uma densidade ruim.

## O que eu recomendo implementar

A arquitetura ideal para a Fuse seria:

### Etapa 1 — Cloud Map correto

Alterar o weather map para possuir semântica compatível com o HDRP:

```text
R = coverage
G = rain / darkness
B = cloud type
A = maximum height
```

A geração procedural atual pode ser mantida, mas os canais precisam ser usados com essas funções.

### Etapa 2 — Cloud LUT

Criar uma LUT com:

```text
R = density profile
G = erosion and shaping
B = ambient occlusion
```

Cada tipo de nuvem ocuparia uma região horizontal da LUT:

```text
0.00 - 0.33 = stratus
0.33 - 0.66 = stratocumulus
0.66 - 1.00 = cumulus
```

Assim, stratus, stratocumulus e cumulus poderiam coexistir no mesmo weather map.

### Etapa 3 — Substituir `SampleCloudDensity`

A função atual deve ser dividida em:

```text
GetCloudCoverageData
EvaluateCloudProfile
EvaluateShapeNoise
EvaluateErosion
EvaluateCloudProperties
```

Isso facilita depuração e permite criar visualizações individuais de:

- coverage;
- type;
- height profile;
- base noise;
- erosion;
- AO;
- density final.

### Etapa 4 — Iluminação volumétrica

Substituir o `silverLining` artificial por:

- Beer-Lambert;
- Henyey-Greenstein;
- forward scattering;
- backward scattering;
- powder effect dependente da densidade;
- multi-scattering aproximado;
- ambient probe superior/inferior;
- light transmittance até a borda real da casca.

Os seis light samples do Robert podem ser mantidos como cone lighting para suavizar o auto-sombreamento.

### Etapa 5 — Raymarch adaptativo

A Unity não usa todos os passos igualmente.

Ela:

- faz amostras normais quando encontra densidade;
- depois de vários espaços vazios, aumenta o passo;
- volta alguns passos quando detecta que entrou novamente na nuvem;
- usa uma versão barata da densidade para o salto.

Isso melhora performance sem sacrificar volume.

### Etapa 6 — Reprojeção e upscale

Depois:

- armazenar profundidade representativa da nuvem;
- usar vizinhança 3×3;
- validar profundidade;
- aplicar neighborhood clipping;
- rejeitar história quando o sol ou o vento mudarem;
- fazer upscale bilateral usando profundidade da cena e profundidade da nuvem.

## Ordem correta de prioridade

A ordem que dará maior ganho visual é:

1. Cloud LUT e perfis verticais;
2. separação entre cobertura, forma e erosão;
3. iluminação solar com transmittance correta;
4. multi-scattering e ambient occlusion;
5. domain warping e redução de repetição;
6. reprojeção temporal e denoising;
7. raymarch adaptativo para recuperar performance.

A mudança mais importante não é aumentar de 64 para 128 passos. É transformar a densidade atual em um sistema baseado em:

```text
weather map
+ cloud LUT
+ macro shape
+ edge erosion
+ light transmittance
+ phase function
+ multi-scattering
```

Minha recomendação é implementar primeiro o modelo `Cloud Map + Cloud LUT + EvaluateCloudProperties`. Essa etapa deve resolver a maior parte da aparência de “pratos voadores” e dar formatos realmente diferentes para stratus, stratocumulus e cumulus.