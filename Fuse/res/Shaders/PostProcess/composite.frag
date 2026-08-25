#include "common.glsl"
#include "bloom.glsl"
#include "motionBlur.glsl"
#include "tonemap.glsl"

void main() {
    if (uPass == 0) {
        FragColor = vec4(texture(uScene, vTexCoord).rgb, 1.0);
    }
    else if (uPass == 1) {
        FragColor = BloomExtract(vTexCoord);
    }
    else if (uPass == 2) {
        FragColor = KawaseStep1(vTexCoord);
    }
    else if (uPass == 3) {
        FragColor = KawaseStep2(vTexCoord);
    }
    else if (uPass == 4) {
        FragColor = BloomComposite(vTexCoord);
    }
    else if (uPass == 5) {
        FragColor = MotionBlur(vTexCoord);
    }
    else if (uPass == 6) {
        FragColor = TonemapOnly(vTexCoord);
    }
}