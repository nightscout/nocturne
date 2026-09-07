<script lang="ts">
    import { onMount } from "svelte";
    import { auroraTime } from "$lib/utils/aurora-noise";
    import {
        MAX_RIPPLES,
        RIPPLE_LIFE,
        RIPPLE_SPEED,
        RIPPLE_WIDTH,
        type RippleField,
    } from "$lib/utils/aurora-ripples";

    let {
        height = 880,
        intensity = 1.0,
        speed = 1.0,
        ripples = null,
        class: className = "",
    }: {
        height?: number;
        intensity?: number;
        speed?: number;
        ripples?: RippleField | null;
        class?: string;
    } = $props();

    let canvasEl: HTMLCanvasElement;

    // Fragment shader: domain-warped FBM noise into the Nocturne glucose palette.
    // aurora-noise.ts is a JS port of this chain; keep the two in step.
    const FRAG = `
precision highp float;
uniform vec2  u_res;
uniform float u_t;
uniform float u_intensity;
uniform vec3  u_rip[${MAX_RIPPLES}];
uniform int   u_ripN;

const float RIP_LIFE  = ${RIPPLE_LIFE.toFixed(4)};
const float RIP_SPEED = ${RIPPLE_SPEED.toFixed(4)};
const float RIP_WIDTH = ${RIPPLE_WIDTH.toFixed(4)};

const vec3 C_VLOW  = vec3(0.835, 0.149, 0.192);
const vec3 C_LOW   = vec3(0.165, 0.608, 0.608);
const vec3 C_IN    = vec3(0.851, 0.455, 0.255);
const vec3 C_TIGHT = vec3(0.298, 0.722, 0.420);
const vec3 C_HIGH  = vec3(0.357, 0.435, 0.902);
const vec3 C_BG    = vec3(0.039, 0.047, 0.071);

float hash(vec2 p){ p = fract(p*vec2(123.34,456.21)); p += dot(p,p+45.32); return fract(p.x*p.y); }
float noise(vec2 p){
  vec2 i=floor(p),f=fract(p);
  float a=hash(i),b=hash(i+vec2(1.,0.)),c=hash(i+vec2(0.,1.)),d=hash(i+vec2(1.,1.));
  vec2 u=f*f*(3.-2.*f);
  return mix(mix(a,b,u.x),mix(c,d,u.x),u.y);
}
float fbm(vec2 p){
  float v=0.,a=0.5;
  for(int i=0;i<5;i++){ v+=a*noise(p); p*=2.03; a*=0.5; }
  return v;
}
vec3 ramp(float t){
  t=clamp(t,0.,1.);
  if(t<0.22) return mix(C_VLOW,C_LOW,smoothstep(0.,0.22,t));
  if(t<0.45) return mix(C_LOW,C_IN,smoothstep(0.22,0.45,t));
  if(t<0.65) return mix(C_IN,C_TIGHT,smoothstep(0.45,0.65,t));
  return mix(C_TIGHT,C_HIGH,smoothstep(0.65,1.,t));
}
// Mirrors rippleCrest() in aurora-ripples.ts.
float crest(float d,float age){
  if(age<0.||age>=RIP_LIFE) return 0.;
  float front=(d-age*RIP_SPEED)/RIP_WIDTH;
  float fade=1.-age/RIP_LIFE;
  return exp(-front*front)*fade*fade;
}
void main(){
  vec2 uv=gl_FragCoord.xy/u_res.xy;
  vec2 p=(gl_FragCoord.xy-0.5*u_res.xy)/u_res.y;
  float t=u_t*0.06;
  vec2 q=vec2(fbm(p*1.4+vec2(0.,t)),fbm(p*1.4+vec2(5.2,-t*0.8)));
  vec2 r=vec2(fbm(p*2.1+1.8*q+vec2(1.7,9.2)+t*1.3),fbm(p*2.1+1.8*q+vec2(8.3,2.8)-t*1.1));
  float n=fbm(p*1.6+2.2*r);
  float yb=p.y*1.15+0.05;
  float band=smoothstep(0.0,0.55,1.0-yb*yb);
  float v=pow(n,1.15)*(0.55+0.6*band);
  vec3 col=ramp(v);
  float vign=smoothstep(0.95,0.2,length(p*vec2(0.55,1.05)));
  col=mix(C_BG,col,vign*u_intensity);
  // Ripples: a red crest at each wavefront, with darker water just behind it.
  float rip=0.,trough=0.;
  for(int i=0;i<${MAX_RIPPLES};i++){
    if(i>=u_ripN) break;
    float d=length(p-u_rip[i].xy);
    float age=u_rip[i].z;
    rip+=crest(d,age);
    trough+=crest(d+1.6*RIP_WIDTH,age);
  }
  rip=clamp(rip,0.,1.);
  col*=1.-0.45*clamp(trough,0.,1.);
  col=mix(col,C_VLOW,rip*(0.6+0.4*n));
  col+=C_VLOW*rip*0.35;
  float g=(hash(gl_FragCoord.xy+u_t*0.001)-0.5)*0.025;
  col+=g;
  gl_FragColor=vec4(col,1.0);
}`;

    const VERT = `attribute vec2 a_pos; void main(){ gl_Position=vec4(a_pos,0.0,1.0); }`;

    onMount(() => {
        const canvas = canvasEl;
        if (!canvas) return;

        const gl = canvas.getContext("webgl", { antialias: false, alpha: false });
        if (!gl) return;

        // A shader that fails to compile or link draws a silent black hero; surface why.
        const compile = (type: number, src: string) => {
            const s = gl.createShader(type)!;
            gl.shaderSource(s, src);
            gl.compileShader(s);
            if (gl.getShaderParameter(s, gl.COMPILE_STATUS)) return s;
            console.error(gl.getShaderInfoLog(s));
            return null;
        };

        const vert = compile(gl.VERTEX_SHADER, VERT);
        const frag = compile(gl.FRAGMENT_SHADER, FRAG);
        if (!vert || !frag) return;
        const prog = gl.createProgram()!;
        gl.attachShader(prog, vert);
        gl.attachShader(prog, frag);
        gl.linkProgram(prog);
        if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
            console.error(gl.getProgramInfoLog(prog));
            return;
        }
        gl.useProgram(prog);

        const buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buf);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
        const loc = gl.getAttribLocation(prog, "a_pos");
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

        const uRes = gl.getUniformLocation(prog, "u_res");
        const uT = gl.getUniformLocation(prog, "u_t");
        const uI = gl.getUniformLocation(prog, "u_intensity");
        const uRip = gl.getUniformLocation(prog, "u_rip[0]");
        const uRipN = gl.getUniformLocation(prog, "u_ripN");
        const ripBuf = new Float32Array(MAX_RIPPLES * 3);

        let raf: number;
        let running = true;
        const dpr = Math.min(window.devicePixelRatio || 1, 2);

        const resize = () => {
            const w = canvas.clientWidth;
            const h = canvas.clientHeight;
            canvas.width = Math.floor(w * dpr);
            canvas.height = Math.floor(h * dpr);
            gl.viewport(0, 0, canvas.width, canvas.height);
        };
        resize();
        window.addEventListener("resize", resize);

        const tick = () => {
            if (!running) return;
            const now = auroraTime();
            gl.uniform2f(uRes, canvas.width, canvas.height);
            gl.uniform1f(uT, now * speed);
            gl.uniform1f(uI, intensity);
            if (ripples) {
                // Ripple ages run on the unscaled clock so the pool's physics sees the same radius.
                gl.uniform1i(uRipN, ripples.pack(now, ripBuf));
                gl.uniform3fv(uRip, ripBuf);
            }
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            raf = requestAnimationFrame(tick);
        };
        tick();

        return () => {
            running = false;
            cancelAnimationFrame(raf);
            window.removeEventListener("resize", resize);
        };
    });
</script>

<canvas
    bind:this={canvasEl}
    class="block w-full object-cover {className}"
    style="height: {height}px"
    aria-hidden="true"
></canvas>
