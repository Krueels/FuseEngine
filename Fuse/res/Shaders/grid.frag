#version 330 core
in vec3 vViewPos;
in vec3 vWorldPos;
in vec3 vWorldNormal;

out vec4 fragColor;

uniform vec3 uColor;
uniform float uFadeDistance;
uniform float uSnapGrid;

void main() {
    vec3 n = abs(normalize(vWorldNormal));
    vec2 coord;
    
    if (n.y > 0.5) coord = vWorldPos.xz;
    else if (n.z > 0.5) coord = vWorldPos.xy;
    else coord = vWorldPos.yz;
    
    // Coordenadas
    vec2 snapCoord = coord / uSnapGrid;
    vec2 coord10 = coord / (uSnapGrid * 10.0);
    
    // Derivadas (densidade na tela)
    vec2 deriv = fwidth(snapCoord);
    vec2 deriv10 = fwidth(coord10);
    
    // Calcula as linhas puras
    vec2 grid = abs(fract(snapCoord - 0.5) - 0.5) / deriv;
    vec2 grid10 = abs(fract(coord10 - 0.5) - 0.5) / deriv10;
    
    float line = min(grid.x, grid.y);
    float line10 = min(grid10.x, grid10.y);
    
    // Intensidade das linhas (1 pixel de espessura)
    float alphaMinor = 1.0 - min(line, 1.0);
    float alphaMajor = 1.0 - min(line10, 1.0);
    
    // Level of Detail (LOD) Fading para evitar Moiré / Ruído pontilhado
    // Desaparece suavemente com as linhas quando a célula do grid se aproxima do tamanho de 1 pixel na tela.
    float density = max(deriv.x, deriv.y);
    float density10 = max(deriv10.x, deriv10.y);
    
    float fadeMinor = 1.0 - smoothstep(0.1, 0.6, density);
    float fadeMajor = 1.0 - smoothstep(0.1, 0.6, density10);
    
    // Combina as linhas aplicando seus respectivos LODs
    // Keep the editor grid readable without competing with the scene. The
    // previous values made the dense far grid look like a second surface.
    float alphaLine = max(alphaMinor * 0.16 * fadeMinor, alphaMajor * 0.36 * fadeMajor);
    
    // Fade de Distância (Horizonte - max 1500 units)
    float depth = abs(vViewPos.z);
    float alphaFade = 1.0 - smoothstep(uFadeDistance * 0.1, uFadeDistance, depth);
    
    float finalAlpha = alphaLine * alphaFade;
    
    if (finalAlpha <= 0.01) {
        discard;
    }
    
    fragColor = vec4(uColor, finalAlpha);
}
