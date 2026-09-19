/*
 * Data-driven weapon effects.
 *
 * Consumes the WeaponEffect/resolve payload: PSDescription emitters placed at the weapon's own
 * holding-location anchors, plus a tinted aura shell mesh. Everything here evaluates the game's own
 * Waveforms and particle keyframes rather than approximating an effect from texture references.
 *
 * DDO is Z-up and the exported scene root is Y-up, so every position and direction from the dats is
 * converted with (x, y, z) -> (x, z, -y).
 *
 * See C:\dev\ddonexus\weapon-visual-effects.md for the formats and the client formulas.
 */
(function () {
  'use strict';

  const textureCache = new Map();

  function ddoToViewer(x, y, z) {
    return new THREE.Vector3(x, z, -y);
  }

  // ── waveforms ───────────────────────────────────────────────────────────

  // Each term is linear in time: base + baseVelocity * t, and so on. Phase is in half-turns.
  // The backend omits zero-valued waveform fields, so every read goes through n().
  function n(v) { return typeof v === 'number' && isFinite(v) ? v : 0; }

  function evalWaveform(w, t) {
    if (!w) return 0;
    if (w.type === 'None' || !w.type) return n(w.base);

    const base = n(w.base) + n(w.baseVelocity) * t;
    const amp = n(w.amplitude) + n(w.amplitudeVelocity) * t;
    const freq = n(w.frequency) + n(w.frequencyVelocity) * t;
    const phase = n(w.phase) * Math.PI + n(w.phaseVelocity) * t;

    switch (w.type) {
      case 'Speed': return base + t * freq + phase;
      case 'Noise': return base + amp * (Math.random() * 2 - 1);
      case 'Sine': return base + amp * Math.sin(t * freq + phase);
      case 'Square': return base + (Math.sin(t * freq + phase) < 0 ? -amp : amp);
      case 'Bounce': return base + amp * Math.abs(Math.sin(t * freq + phase));
      // Perlin and Fractal are approximated; nothing in the weapon-imbue path uses them.
      case 'Perlin':
      case 'Fractal': return base + amp * Math.sin(t * freq + phase) * 0.6;
      case 'Keyframe': return evalKeyframes(w, t);
      default: return base;
    }
  }

  function evalKeyframes(w, t) {
    const keys = w.keyframes || [];
    if (!keys.length) return n(w.base);
    const duration = n(w.keyframeDuration) > 0 ? w.keyframeDuration : 1;
    const elapsed = w.keyframeLoops ? (t % duration) : Math.min(t, duration);
    const pct = elapsed / duration * 100;
    if (pct <= n(keys[0].percent)) return n(keys[0].value);
    for (let i = 1; i < keys.length; i++) {
      if (pct <= n(keys[i].percent)) {
        const p0 = n(keys[i - 1].percent), v0 = n(keys[i - 1].value), p1 = n(keys[i].percent), v1 = n(keys[i].value);
        const f = p1 === p0 ? 0 : (pct - p0) / (p1 - p0);
        return v0 + (v1 - v0) * f;
      }
    }
    return n(keys[keys.length - 1].value);
  }

  function vector3At(waves, t) {
    if (!waves) return new THREE.Vector3(0, 0, 0);
    return ddoToViewer(evalWaveform(waves.x, t), evalWaveform(waves.y, t), evalWaveform(waves.z, t));
  }

  // ── particle keyframes (size and colour over a particle's life) ─────────

  function colorFromHex(argb) {
    const v = (typeof argb === 'string' ? parseInt(argb.replace(/^0x/i, ''), 16) : argb) || 0;
    return {
      a: ((v >>> 24) & 0xff) / 255,
      r: ((v >>> 16) & 0xff) / 255,
      g: ((v >>> 8) & 0xff) / 255,
      b: (v & 0xff) / 255,
    };
  }

  function prepareKeys(keys) {
    return (keys || []).map(k => {
      const c = colorFromHex(k.color);
      return { time: k.time, scaleX: k.scaleX, scaleY: k.scaleY, r: c.r, g: c.g, b: c.b, a: c.a };
    }).sort((x, y) => x.time - y.time);
  }

  function sampleKeys(keys, age) {
    if (!keys.length) return { scaleX: 1, scaleY: 1, r: 1, g: 1, b: 1, a: 1 };
    if (age <= keys[0].time) return keys[0];
    for (let i = 1; i < keys.length; i++) {
      if (age <= keys[i].time) {
        const a = keys[i - 1], b = keys[i];
        const span = b.time - a.time;
        const f = span <= 0 ? 0 : (age - a.time) / span;
        return {
          scaleX: a.scaleX + (b.scaleX - a.scaleX) * f,
          scaleY: a.scaleY + (b.scaleY - a.scaleY) * f,
          r: a.r + (b.r - a.r) * f,
          g: a.g + (b.g - a.g) * f,
          b: a.b + (b.b - a.b) * f,
          a: a.a + (b.a - a.a) * f,
        };
      }
    }
    return keys[keys.length - 1];
  }

  // ── textures ────────────────────────────────────────────────────────────

  function loadTexture(url) {
    if (!url) return Promise.resolve(null);
    if (textureCache.has(url)) return textureCache.get(url);
    const p = new Promise(resolve => {
      new THREE.TextureLoader().load(url, tex => {
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        // Left undecoded on purpose: the whole viewer works in display space, like the client, so the
        // renderer writes these bytes back out unchanged. See applyDisplaySpaceTextures in index.html.
        if (THREE.LinearEncoding !== undefined) tex.encoding = THREE.LinearEncoding;
        resolve(tex);
      }, undefined, () => resolve(null));
    });
    textureCache.set(url, p);
    return p;
  }

  // ── emitters ────────────────────────────────────────────────────────────

  // One pool per (anchor, emitter). Each particle owns its material and a cloned texture so it can
  // sit on its own frame of the sprite sheet.
  function makeEmitter(container, emitter, texture, anchorDdo) {
    const uFrames = Math.max(1, emitter.numUFrames || 1);
    const vFrames = Math.max(1, emitter.numVFrames || 1);
    const capacity = Math.max(1, Math.min(emitter.maxParticles || 1, 64));
    const particles = [];

    for (let i = 0; i < capacity; i++) {
      const tex = texture ? texture.clone() : null;
      if (tex) {
        tex.needsUpdate = true;
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        tex.repeat.set(1 / uFrames, 1 / vFrames);
      }
      const material = new THREE.SpriteMaterial({
        map: tex, color: 0xffffff, transparent: true, depthWrite: false,
        depthTest: true, blending: THREE.AdditiveBlending, opacity: 1,
      });
      material.alphaTest = 0.004;
      if ('toneMapped' in material) material.toneMapped = false;

      const sprite = new THREE.Sprite(material);
      sprite.visible = false;
      sprite.userData.ddoVfx = true;
      container.add(sprite);

      particles.push({
        sprite, material, tex, alive: false, age: 0, life: 1, size: 0.1,
        velocity: new THREE.Vector3(), rotation: 0, rotationVelocity: 0, startFrame: 0,
      });
    }

    return {
      emitter,
      anchor: ddoToViewer(anchorDdo[0] || 0, anchorDdo[1] || 0, anchorDdo[2] || 0),
      keys: prepareKeys(emitter.keyframes),
      uFrames, vFrames,
      frames: uFrames * vFrames,
      particles,
      spawnAccumulator: 0,
      time: 0,
      // BirthRate reads as seconds between spawns; the client derives particles-per-second from it.
      interval: n(emitter.birthRate) > 0 ? emitter.birthRate : 0.1,
    };
  }

  function spawnParticle(state, p) {
    const e = state.emitter, t = state.time;

    const direction = vector3At(e.direction, t);
    if (direction.lengthSq() < 1e-8) direction.set(0, 1, 0);
    direction.normalize();

    const minSpread = evalWaveform(e.minSpread, t);
    const maxSpread = evalWaveform(e.maxSpread, t);
    const spread = THREE.MathUtils.degToRad(minSpread + Math.random() * Math.max(0, maxSpread - minSpread));
    const axis = new THREE.Vector3(Math.random() - 0.5, Math.random() - 0.5, Math.random() - 0.5);
    if (axis.lengthSq() < 1e-8) axis.set(1, 0, 0);
    direction.applyAxisAngle(axis.normalize(), spread);

    // The emitter volume: Scale is its extent, OriginOffset shifts the whole emitter.
    const radius = Math.max(0, evalWaveform(e.scale, t));
    const offset = vector3At(e.originOffset, t);
    p.sprite.position.copy(state.anchor).add(offset).add(new THREE.Vector3(
      (Math.random() - 0.5) * radius,
      (Math.random() - 0.5) * radius,
      (Math.random() - 0.5) * radius));

    p.velocity.copy(direction).multiplyScalar(evalWaveform(e.velocity, t));
    p.life = Math.max(0.05, evalWaveform(e.lifespan, t));
    p.size = Math.max(0.001, Math.abs(evalWaveform(e.particleScale, t))) * tuning.particleScale;
    p.rotation = THREE.MathUtils.degToRad(evalWaveform(e.rotation && e.rotation.z, t));
    p.rotationVelocity = THREE.MathUtils.degToRad(evalWaveform(e.rotationVelocity && e.rotationVelocity.z, t));
    p.startFrame = e.randomizeStartFrame ? Math.floor(Math.random() * state.frames) : 0;
    p.age = 0;
    p.alive = true;
    p.sprite.visible = true;
  }

  function updateEmitter(state, dt) {
    state.time += dt;

    // Spawn: one particle per interval, reusing dead slots.
    state.spawnAccumulator += dt;
    while (state.spawnAccumulator >= state.interval) {
      state.spawnAccumulator -= state.interval;
      const free = state.particles.find(p => !p.alive);
      if (free) spawnParticle(state, free);
    }

    for (const p of state.particles) {
      if (!p.alive) continue;

      p.age += dt;
      if (p.age >= p.life) {
        p.alive = false;
        p.sprite.visible = false;
        continue;
      }

      p.sprite.position.addScaledVector(p.velocity, dt);
      p.rotation += p.rotationVelocity * dt;

      const k = sampleKeys(state.keys, p.age);
      p.material.color.setRGB(k.r, k.g, k.b);
      p.material.opacity = Math.max(0, Math.min(1, k.a));
      p.material.rotation = p.rotation;
      p.sprite.scale.set(p.size * k.scaleX, p.size * k.scaleY, 1);

      if (p.tex && state.frames > 1) {
        const frame = (p.startFrame + Math.floor(p.age * (state.emitter.framesPerSec || 0))) % state.frames;
        const col = frame % state.uFrames;
        const row = Math.floor(frame / state.uFrames);
        p.tex.offset.set(col / state.uFrames, 1 - (row + 1) / state.vFrames);
      }
    }
  }

  // ── aura shell ──────────────────────────────────────────────────────────

  // The aura material carries two independently transformed copies of one texture (DiffuseMap and
  // DiffuseMap2). Inside the shader they ADD, and then the whole shader runs twice - the client issues
  // two identical draws of this mesh per frame (confirmed in a RenderDoc capture: same VS, PS, textures,
  // constants, depth and raster state, 21 chunks apart). Both facts come from the client's own data, not
  // from guesswork; earlier builds inferred a multiply and rendered a flat dark cyan.
  const AURA_PASSES = 2;

  const AURA_VERTEX_SHADER = `
    varying vec2 vDdoUv;
    void main() {
      vDdoUv = uv;
      gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }`;

  const AURA_FRAGMENT_SHADER = `
    uniform sampler2D map1;
    uniform sampler2D map2;
    uniform mat3 uvTransform1;
    uniform mat3 uvTransform2;
    uniform vec3 tint;
    uniform float alpha;
    uniform float intensity;
    varying vec2 vDdoUv;

    // A direct port of the client's own pixel shader, disassembled out of RenderMaterial 0x2B000325:
    //
    //     sample r0, v2.xyxx, t0, s0        // Texture_Color  through c_TextureMatrix0
    //     sample r1, v2.zwzz, t1, s1        // Texture_Color2 through c_TextureMatrix1
    //     add    r0.xyz, r0.xyzx, r1.xyzx   // the layers add
    //     mul    o0.xyz, r0.xyzx, cb0[9].xyzx   // x c_MaterialDiffuseColor.rgb
    //     mov    o0.w,   cb0[9].w               // alpha is the constant DiffuseColor.a
    //
    // No gamma conversions: the client samples and blends in display space, and a raw ShaderMaterial
    // receives none of three's decode/encode injection, so what this writes is what is displayed.
    void main() {
      vec2 uv1 = (uvTransform1 * vec3(vDdoUv, 1.0)).xy;
      vec2 uv2 = (uvTransform2 * vec3(vDdoUv, 1.0)).xy;
      vec3 a = texture2D(map1, uv1).rgb;
      vec3 b = texture2D(map2, uv2).rgb;
      gl_FragColor = vec4((a + b) * tint * intensity, alpha);
    }`;

  // The aura and particle shaders above are ports of the client's own, disassembled from the material
  // records, and the blend state and pass count are read off a capture, so there is nothing left to tune
  // about how they combine. What remains are viewer conveniences: a debug multiplier that should stay at
  // 1 for fidelity, and the strength of the Render_Color tint, whose role in the client is inferred
  // rather than confirmed (nothing in the capture carries it as a material constant).
  const tuning = {
    auraIntensity: 1.0,
    particleScale: 1.0,
    baseTintStrength: 1.0,
  };

  function uvMatrix(offsetX, offsetY, repeatX, repeatY, rotation) {
    const c = Math.cos(rotation), s = Math.sin(rotation);
    // The client's own convention, read straight off c_TextureMatrix0/1 in a RenderDoc capture:
    //
    //   u' =  sx*cos*u + sy*sin*v + offU
    //   v' = -sx*sin*u + sy*cos*v + offV
    //
    // Rotation is about the UV ORIGIN and there are no 0.5 terms anywhere. Verified against the captured
    // values: layer 1 (no rotation) is [1.0, 0, 0.2364] / [0, 0.7, 57.0621], and layer 2 decomposes to
    // 1.3*cos = 0.1407, 1.3*sin = 1.2924, i.e. scale 1.3 at 83.8 degrees.
    //
    // This used to build THREE.Texture.matrix form, which rotates about the texture CENTRE. That is not
    // a cosmetic difference: the shell's UVs only span u 0.028-0.493, so the sampled patch sits ~0.24
    // from a centre pivot but ~0.56 from the origin pivot. Same angular rate, less than half the linear
    // sweep, which reads as the whole effect spinning in smooth slow motion next to the game.
    return new THREE.Matrix3().set(
      repeatX * c, repeatY * s, offsetX,
      -repeatX * s, repeatY * c, offsetY,
      0, 0, 1);
  }

  function addShell(handle, shell, container) {
    if (!shell || !shell.glbUrl || typeof THREE.GLTFLoader !== 'function') return;

    const layers = (shell.layers || []).filter(x => x && x.textureUrl);
    if (!layers.length) return;

    new THREE.GLTFLoader().load(shell.glbUrl, gltf => {
      if (handle.disposed) return;

      Promise.all(layers.map(layer => loadTexture(layer.textureUrl))).then(textures => {
        if (handle.disposed) return;

        const tint = shell.tint || [1, 1, 1];
        const map1 = textures[0] || null;
        const map2 = textures[1] || textures[0] || null;
        if (!map1) return;

        const uniforms = {
          map1: { value: map1 },
          map2: { value: map2 },
          uvTransform1: { value: new THREE.Matrix3() },
          uvTransform2: { value: new THREE.Matrix3() },
          tint: { value: new THREE.Color(tint[0], tint[1], tint[2]) },
          alpha: { value: shell.opacity != null ? shell.opacity : 1 },
          intensity: { value: tuning.auraIntensity },
        };

        const material = new THREE.ShaderMaterial({
          uniforms,
          vertexShader: AURA_VERTEX_SHADER,
          fragmentShader: AURA_FRAGMENT_SHADER,
          transparent: true,
          depthWrite: !!shell.depthWrite,
          depthTest: shell.depthTest !== false,
          side: shell.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
          // SrcAlpha/One, read off a RenderDoc capture of the client (SrcBlend=SRC_ALPHA, BlendOp=ADD,
          // DestBlend=ONE) - which is exactly three's AdditiveBlending.
          blending: THREE.AdditiveBlending,
        });
        if ('toneMapped' in material) material.toneMapped = false;

        // The client draws this mesh TWICE per frame with identical state: same shader, textures,
        // constants and matrices (RenderDoc chunkIndex 1336 and 1357). The material carries two layers
        // and each one is its own draw, so the additive result lands twice.
        const root = gltf.scene;
        root.name = 'DDO Weapon Aura';
        root.userData.ddoVfx = true;
        // No scale nudge. The shell is its own mesh at its own transform, and the client applies
        // nothing here - it relies on DepthWrite=false instead. Scaling to "avoid z-fighting" scales
        // about the MODEL ORIGIN, not the mesh centre, so every vertex slides outward in proportion to
        // its distance from that origin; on a staff, whose origin sits at the handle, that walks the
        // whole shell up the shaft and leaves base geometry poking through the bottom of the blades.

        const meshes = [];
        root.traverse(o => {
          o.userData.ddoVfx = true;
          if (o.isMesh) { o.material = material; meshes.push(o); }
        });

        // Extra passes are explicit sibling meshes sharing geometry and material rather than a scene
        // clone - equivalent here, just with no dependence on how clone() treats the hierarchy.
        //
        // When checking this against a capture, predict the pixels by sampling the texture through the
        // REAL UV matrices, not at independent random texels. Random sampling makes bright-plus-bright
        // coincidences far likelier than the actual transforms do, which over-predicts the result badly
        // enough to look like a whole missing pass.
        //
        // The passes cannot be collapsed into one draw with the gain folded into the shader: a fragment's
        // colour is clamped to [0,1] BEFORE blending, so where (tex0+tex1)*tint exceeds 0.5 - which blue
        // does across much of this texture - one doubled pass saturates at 0.6 while two passes deliver
        // the full 1.2x.
        for (const mesh of meshes) {
          for (let pass = 1; pass < AURA_PASSES; pass++) {
            const dup = new THREE.Mesh(mesh.geometry, material);
            dup.name = mesh.name + ' pass' + (pass + 1);
            dup.userData.ddoVfx = true;
            dup.position.copy(mesh.position);
            dup.quaternion.copy(mesh.quaternion);
            dup.scale.copy(mesh.scale);
            dup.frustumCulled = false;
            (mesh.parent || root).add(dup);
          }
        }

        container.add(root);
        handle.shellRoots.push(root);

        handle.shellMaterials.push(material);
        handle.shellUniforms = uniforms;
        handle.shellLayers = layers;
      });
    }, undefined, () => {});
  }

  function updateShell(handle, time) {
    const uniforms = handle.shellUniforms;
    if (!uniforms) return;

    uniforms.intensity.value = tuning.auraIntensity;

    handle.shellLayers.forEach((layer, index) => {
      const target = index === 0 ? uniforms.uvTransform1 : uniforms.uvTransform2;
      if (!target) return;
      target.value.copy(uvMatrix(
        evalWaveform(layer.uTranslate, time),
        evalWaveform(layer.vTranslate, time),
        evalWaveform(layer.uScale, time) || 1,
        evalWaveform(layer.vScale, time) || 1,
        // UVRotate is degrees per second and turns NEGATIVE. Both facts are pinned by the capture
        // rather than guessed: layer 1's vTranslate (0.3 + 0.5t) fixes t = 113.5242s from its captured
        // offset of 57.0621, and at that single t the other two waveforms reproduce their captured
        // values exactly - uTranslate 0.2365 against 0.2364, and rotation -50t giving 1.3*cos = 0.1406
        // and 1.3*sin = +1.2924 against +0.1407 / +1.2924. With +50t the sine term comes out -1.2924,
        // the wrong sign, so the effect counter-rotates.
        -THREE.MathUtils.degToRad(evalWaveform(layer.uvRotate, time))));
    });
  }

  // Render_Color tints the base mesh in the client, so an imbued weapon reads as coloured metal even
  // where the aura does not cover it. Originals are kept so clearing the effects restores the model.
  function applyBaseTint(handle, tint, model) {
    if (!tint || !model) return;
    handle.baseTint = tint;
    model.traverse(o => {
      if (!o.isMesh || o.userData.ddoVfx) return;
      const materials = Array.isArray(o.material) ? o.material : [o.material];
      for (const material of materials) {
        if (!material || !material.color) continue;
        handle.tintedMaterials.push({ material, r: material.color.r, g: material.color.g, b: material.color.b });
      }
    });
    updateBaseTint(handle, true);
  }

  function updateBaseTint(handle, force) {
    if (!handle.baseTint || !handle.tintedMaterials.length) return;
    const strength = tuning.baseTintStrength;
    if (!force && strength === handle.appliedTintStrength) return;
    handle.appliedTintStrength = strength;

    const t = handle.baseTint;
    for (const entry of handle.tintedMaterials) {
      entry.material.color.setRGB(
        entry.r * (1 - strength + strength * t[0]),
        entry.g * (1 - strength + strength * t[1]),
        entry.b * (1 - strength + strength * t[2]));
    }
  }

  function restoreBaseTint(handle) {
    for (const entry of handle.tintedMaterials)
      entry.material.color?.setRGB(entry.r, entry.g, entry.b);
    handle.tintedMaterials = [];
  }

  // ── public API ──────────────────────────────────────────────────────────

  function createSystems(data, container, baseModel) {
    const handle = {
      container, emitters: [], shellRoots: [], shellUniforms: null, shellLayers: [],
      shellMaterials: [], tintedMaterials: [], baseTint: null, appliedTintStrength: null,
      time: 0, startedAt: null, disposed: false,
      update(dt) { updateHandle(this, dt); },
      dispose() { disposeHandle(this); },
    };

    if (!data || !container) return handle;

    const systems = data.particleSystems || {};
    const wanted = new Map();
    for (const spawn of data.spawns || []) {
      const system = systems[spawn.particleSystem];
      if (!system) continue;
      if (!wanted.has(system.id || spawn.particleSystem)) wanted.set(system.id || spawn.particleSystem, system);
    }

    // Textures first, so every pool for a system shares one decoded image.
    const urls = new Map();
    for (const [id, system] of wanted) urls.set(id, loadTexture(system.textureUrl));

    for (const spawn of data.spawns || []) {
      const system = systems[spawn.particleSystem];
      if (!system) continue;
      const texturePromise = urls.get(system.id || spawn.particleSystem) || Promise.resolve(null);
      texturePromise.then(texture => {
        if (handle.disposed) return;
        for (const emitter of system.emitters || [])
          handle.emitters.push(makeEmitter(container, emitter, texture, spawn.position || [0, 0, 0]));
      });
    }

    addShell(handle, data.shell, container);
    applyBaseTint(handle, data.baseTint, baseModel);
    return handle;
  }

  function updateHandle(handle, dt) {
    if (handle.disposed) return;

    // Particles integrate velocity, so they need the caller's clamped dt - a huge delta after the window
    // is backgrounded would teleport them. The shell's UV transforms are different: each one is a
    // Waveform evaluated at an absolute time, a pure function with nothing to integrate. Driving them
    // off accumulated clamped dt made them run slower than real time during any frame-rate dip (the
    // caller clamps to 0.05s, so anything under 20fps loses time and the spin visibly drags, which the
    // client never does). Wall clock keeps them correct regardless of frame rate.
    handle.time += dt;
    const now = (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now();
    if (handle.startedAt == null) handle.startedAt = now;

    for (const state of handle.emitters) updateEmitter(state, dt);
    updateShell(handle, (now - handle.startedAt) / 1000);
    updateBaseTint(handle, false);
  }

  function disposeHandle(handle) {
    handle.disposed = true;

    for (const state of handle.emitters) {
      for (const p of state.particles) {
        p.sprite.parent?.remove(p.sprite);
        p.tex?.dispose?.();
        p.material?.dispose?.();
      }
    }
    handle.emitters = [];

    // The passes share geometry (the second root is a clone), so dispose each buffer once.
    const geometries = new Set();
    for (const root of handle.shellRoots) {
      root.parent?.remove(root);
      root.traverse(o => { if (o.geometry) geometries.add(o.geometry); });
    }
    for (const g of geometries) g.dispose?.();
    handle.shellRoots = [];
    handle.shellUniforms = null;
    handle.shellLayers = [];

    for (const m of handle.shellMaterials) m.dispose?.();
    handle.shellMaterials = [];

    restoreBaseTint(handle);
  }

  window.ddoVfx = {
    tuning,
    createSystems,
    /// True when a payload is a resolved effect set rather than the legacy heuristic evidence.
    isResolved(data) {
      return !!data && (Array.isArray(data.spawns) && data.spawns.length > 0
        || !!(data.shell && data.shell.glbUrl) || Array.isArray(data.baseTint));
    },
    evalWaveform,
  };
})();
