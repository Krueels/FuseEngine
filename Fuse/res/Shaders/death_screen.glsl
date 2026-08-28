#version 330 core
layout(location = 0) out vec4 FragColor;

uniform sampler2D uScene;
uniform vec2 uResolution;
uniform float uTime;
uniform float uDeathFade;
uniform float uDeathTimer;

in vec2 vTexCoord;

float character(int n, vec2 p)
{
    p = floor(p * vec2(-4.0, 4.0) + 2.5);
    if (clamp(p.x, 0.0, 4.0) == p.x)
    {
        if (clamp(p.y, 0.0, 4.0) == p.y)
        {
            int a = int(round(p.x) + 5.0 * round(p.y));
            if (((n >> a) & 1) == 1) return 1.0;
        }
    }
    return 0.0;
}

void main()
{
    // Pixela: agrupa pixels em blocos de 8x8
    vec2 blockPixel = floor(gl_FragCoord.xy / 8.0) * 8.0 + 4.0;
    vec2 uv = blockPixel / uResolution;
    vec3 col = texture(uScene, uv).rgb;

    // Grayscale (luminancia)
    float gray = dot(col, vec3(0.299, 0.587, 0.114));

    // Mapeia brightness → caractere ASCII
    int n = 4096;
    if (gray > 0.1) n = 65600;     // :
    if (gray > 0.2) n = 163153;    // *
    if (gray > 0.3) n = 15255086;  // o
    if (gray > 0.4) n = 13121101;  // &
    if (gray > 0.5) n = 15252014;  // 8
    if (gray > 0.6) n = 13195790;  // @
    if (gray > 0.7) n = 11512810;  // #

    // Coordenada 5x5 dentro do bloco de 8x8
    vec2 charCoord = mod(gl_FragCoord.xy, 8.0) / 8.0;
    vec2 p = charCoord * 2.0 - 1.0;

    // 1.0 onde tem pixel do caractere, 0.0 onde não tem
    float mask = character(n, p);

    // Cor do caractere (cena original colorida)
    vec3 charColor = col * mask;

    // Tinta vermelha da morte (pulsa)
    float pulse = 0.5 + 0.5 * sin(uDeathTimer * 2.0);
    vec3 deathTint = vec3(0.8, 0.05, 0.05) * (0.7 + 0.3 * pulse);

    // Aplica tinta vermelha sobre os caracteres
    vec3 asciiResult = charColor * mix(vec3(1.0), deathTint, 0.6);

    // Fundo: preto onde tem caractere, escuro onde não tem
    vec3 bg = vec3(0.0);
    vec3 withBg = mix(bg, asciiResult, mask);

    // Vignette
    vec2 vigUV = vTexCoord;
    float vignette = 1.0 - dot(vigUV - 0.5, vigUV - 0.5) * 1.5;
    withBg *= clamp(vignette, 0.0, 1.0);

    // Fade: interpola entre cena original e efeito ASCII
    vec3 original = texture(uScene, vTexCoord).rgb;
    vec3 result = mix(original, withBg, uDeathFade);

    // Blackout gradual depois de 4 segundos
    float blackout = smoothstep(4.0, 7.0, uDeathTimer);
    result = mix(result, vec3(0.0), blackout);

    FragColor = vec4(result, 1.0);
}