/* Geometry only. Story descriptions and live scene identities come from the host. */
window.EraMapWorld = (() => {
  'use strict';
  const input = window.EraSceneMapInput || {};
  const layout = input.layout && input.layout.format_version === 1 ? input.layout : null;
  const terrain = [], solids = [], edges = [], river = [], routes = [];
  const list = value => Array.isArray(value) ? value : [];
  const finite = (value, fallback = 0) => typeof value === 'number' && Number.isFinite(value) ? value : fallback;
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const positiveId = value => Number.isSafeInteger(value) && value > 0 ? value : null;
  const noise = (a, b = 0) => {
    const value = Math.sin(a * 127.1 + b * 311.7 + 31.481) * 43758.5453123;
    return value - Math.floor(value);
  };
  const gaussian = (x, z, cx, cz, sx, sz) => Math.exp(-(((x - cx) / sx) ** 2 + ((z - cz) / sz) ** 2));
  const point = value => Array.isArray(value) && value.length >= 2 && value.slice(0, 2).every(v => typeof v === 'number' && Number.isFinite(v));
  const polygon = value => list(value).filter(point).map(p => [p[0], p[1]]);
  const candidateBounds = layout && layout.bounds || {};
  const bounds = {
    minX: finite(candidateBounds.minX, -110), maxX: finite(candidateBounds.maxX, 110),
    minZ: finite(candidateBounds.minZ, -110), maxZ: finite(candidateBounds.maxZ, 110)
  };
  if (bounds.maxX - bounds.minX < 16 || bounds.maxZ - bounds.minZ < 16) Object.assign(bounds, {minX: -110, maxX: 110, minZ: -110, maxZ: 110});
  const layoutSites = new Map(list(layout && layout.sites).filter(s => s && positiveId(s.id)).map(s => [s.id, s]));
  const ownerMap = new Map(), seenIds = new Set();
  // Scene IDs remain host/save identities. Only compact IDs enter Float32/GL.
  const sourceSites = list(input.sites).filter(s => {
    if (!s || !positiveId(s.id) || seenIds.has(s.id)) return false;
    seenIds.add(s.id); return true;
  }).slice(0, 192);
  const autoPosition = id => {
    // A scene's placement does not depend on the number/order of known scenes.
    const angle = noise(id, 717) * Math.PI * 2, radius = 14 + noise(id, 823) * 44;
    return [Math.cos(angle) * radius, Math.sin(angle) * radius];
  };
  const sites = sourceSites.map((source, index) => {
    const renderId = index + 1;
    const layoutId = positiveId(source.layoutId), definition = layoutSites.get(layoutId), map = source.map || {};
    let x, z;
    if (definition && Number.isFinite(definition.x) && Number.isFinite(definition.z)) {
      x = definition.x; z = definition.z;
      if (!ownerMap.has(layoutId)) ownerMap.set(layoutId, renderId);
    } else if (Number.isFinite(map.x) && Number.isFinite(map.y)) {
      x = (clamp(map.x, 0, 1) - .5) * (bounds.maxX - bounds.minX) * .68 + (bounds.minX + bounds.maxX) / 2;
      z = (clamp(map.y, 0, 1) - .5) * (bounds.maxZ - bounds.minZ) * .68 + (bounds.minZ + bounds.maxZ) / 2;
    } else [x, z] = autoPosition(source.id);
    const kind = String(definition && definition.kind || map.symbol || source.kind || 'place').toLowerCase();
    const symbol = String(map.symbol || definition && definition.symbol || kind).toLowerCase();
    const radius = clamp(finite(definition && definition.radius, kind === 'forest' ? 12 : 7), 1, 80);
    let outline = polygon(definition && definition.outline);
    if (outline.length < 3) outline = Array.from({length: 8}, (_, i) => [x + Math.cos(i * Math.PI / 4) * radius, z + Math.sin(i * Math.PI / 4) * radius]);
    return {...source, layoutId, renderId, x, z, radius, outline, kind, symbol,
      markerHeight: clamp(finite(definition && definition.marker_height, ['cabin', 'house', 'building', 'market', 'village', 'town'].includes(kind) ? 5 : .4), .15, 40),
      hasLayout: !!definition && Number.isFinite(definition.x) && Number.isFinite(definition.z)};
  });
  const resolveOwner = owner => owner === 0 ? 0 : ownerMap.has(owner) ? ownerMap.get(owner) : null;
  const resolveFeatures = name => list(layout && layout[name]).filter(f => f && resolveOwner(f.owner) !== null).map(f => ({...f, owner: resolveOwner(f.owner)}));
  const riverFeatures = resolveFeatures('rivers');
  const routeFeatures = resolveFeatures('routes');
  const fallbackSites = sites.filter(s => !s.hasLayout);
  for (const site of fallbackSites) {
    if (['stream', 'river', 'water'].includes(site.symbol)) {
      riverFeatures.push({owner: site.renderId, points: [[site.x - 2, site.z - 12], [site.x + 1, site.z - 5], [site.x - 1, site.z + 3], [site.x + 2, site.z + 12]], half_width: 1, offset: .13, valley_depth: .65, valley_width: 3});
    } else if (['path', 'road'].includes(site.symbol)) {
      routeFeatures.push({owner: site.renderId, points: [[site.x - 7, site.z + 4], [site.x, site.z], [site.x + 8, site.z - 5]], half_width: .4, offset: .16});
    }
  }
  const centerlineX = (feature, z) => {
    const curve = feature.centerline || {};
    return finite(curve.offset) + finite(curve.slope) * z + list(curve.waves).reduce((sum, wave) => sum + finite(wave.amplitude) * Math.sin((z + finite(wave.phase)) / Math.max(.1, Math.abs(finite(wave.period, 1)))), 0);
  };
  function smoothPolyline(points) {
    if (points.length < 2) return points;
    const result = [];
    for (let i = 0; i < points.length - 1; i++) {
      const p0 = points[Math.max(0, i - 1)], p1 = points[i], p2 = points[i + 1], p3 = points[Math.min(points.length - 1, i + 2)];
      const steps = Math.max(5, Math.min(512, Math.ceil(Math.hypot(p2[0] - p1[0], p2[1] - p1[1]))));
      for (let k = 0; k < steps; k++) {
        const t = k / steps, t2 = t * t, t3 = t2 * t;
        const axis = c => .5 * (2 * p1[c] + (-p0[c] + p2[c]) * t + (2 * p0[c] - 5 * p1[c] + 4 * p2[c] - p3[c]) * t2 + (-p0[c] + 3 * p1[c] - 3 * p2[c] + p3[c]) * t3);
        result.push([axis(0), axis(1)]);
      }
    }
    result.push(points[points.length - 1]); return result;
  }
  for (const feature of riverFeatures) {
    if (feature.centerline) {
      feature.path = [];
      const start = finite(feature.min_z, bounds.minZ), end = finite(feature.max_z, bounds.maxZ), step = clamp(finite(feature.step, 1), .25, 10);
      for (let z = start; z <= end && feature.path.length < 4096; z += step) feature.path.push([centerlineX(feature, z), z]);
    } else feature.path = smoothPolyline(polygon(feature.points));
  }
  function distanceToPath(x, z, points) {
    let distance = Infinity;
    for (let i = 1; i < points.length; i++) {
      const a = points[i - 1], b = points[i], dx = b[0] - a[0], dz = b[1] - a[1], length = dx * dx + dz * dz;
      const t = length ? clamp(((x - a[0]) * dx + (z - a[1]) * dz) / length, 0, 1) : 0;
      distance = Math.min(distance, Math.hypot(x - a[0] - t * dx, z - a[1] - t * dz));
    }
    return distance;
  }
  const terrainSettings = layout && layout.terrain || {};
  function heightAt(x, z) {
    if (!Number.isFinite(x) || !Number.isFinite(z)) return 0;
    let height = finite(terrainSettings.base_height, 2.9) + (z - finite(terrainSettings.slope_origin_z)) * finite(terrainSettings.slope_z);
    for (const hill of list(terrainSettings.hills)) height += finite(hill.height) * gaussian(x, z, finite(hill.x), finite(hill.z), Math.max(.1, Math.abs(finite(hill.width_x, 20))), Math.max(.1, Math.abs(finite(hill.width_z, 20))));
    for (const ridge of list(terrainSettings.ridges)) {
      const ridgeLine = finite(ridge.x) + finite(ridge.wave_amplitude) * Math.sin(z / Math.max(.1, Math.abs(finite(ridge.wave_period, 20)))) + finite(ridge.slope_z) * z;
      const dx = x - ridgeLine, width = dx < 0 ? finite(ridge.width_left, 4) : finite(ridge.width_right, 20);
      const minimum = clamp(finite(ridge.min_strength, .5), 0, 1);
      height += finite(ridge.height) * Math.exp(-Math.abs(dx) / Math.max(.1, Math.abs(width))) * (minimum + (1 - minimum) * Math.exp(-(((z - finite(ridge.center_z)) / Math.max(.1, Math.abs(finite(ridge.length_z, 45)))) ** 2)));
    }
    for (const feature of riverFeatures) {
      const distance = feature.centerline && z >= finite(feature.min_z, bounds.minZ) && z <= finite(feature.max_z, bounds.maxZ) ? Math.abs(x - centerlineX(feature, z)) : distanceToPath(x, z, feature.path);
      height -= finite(feature.valley_depth) * Math.exp(-((distance / Math.max(.1, Math.abs(finite(feature.valley_width, 4)))) ** 2));
    }
    height += .35 * Math.sin(x * .18 + z * .11) * Math.cos(z * .13 - x * .04) + .20 * Math.sin(x * .39 - z * .17);
    return clamp(height, finite(terrainSettings.min_height, .35), finite(terrainSettings.max_height, 24));
  }
  function triangle(target, a, b, c, owner = 0) {
    const ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
    let nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
    const length = Math.hypot(nx, ny, nz) || 1; nx /= length; ny /= length; nz /= length;
    for (const p of [a, b, c]) target.push(p[0], p[1], p[2], nx, ny, nz, owner);
  }
  function topTriangle(target, a, b, c, owner = 0) {
    if ((b[0] - a[0]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[0] - a[0]) > 0) triangle(target, a, c, b, owner);
    else triangle(target, a, b, c, owner);
  }
  function line(a, b, owner = 0) { for (const p of [a, b]) edges.push(p[0], p[1], p[2], owner); }
  const columns = clamp(Math.round(finite(terrainSettings.columns, 62)), 8, 100), rows = clamp(Math.round(finite(terrainSettings.rows, 58)), 8, 100), grid = [];
  for (let j = 0; j <= rows; j++) {
    const row = [];
    for (let i = 0; i <= columns; i++) {
      const border = i === 0 || j === 0 || i === columns || j === rows;
      const x = bounds.minX + i * (bounds.maxX - bounds.minX) / columns + (border ? 0 : (noise(i, j) - .5) * 1.44);
      const z = bounds.minZ + j * (bounds.maxZ - bounds.minZ) / rows + (border ? 0 : (noise(j + 91, i + 33) - .5) * 1.48);
      row.push([x, heightAt(x, z), z]);
    }
    grid.push(row);
  }
  for (let j = 0; j < rows; j++) for (let i = 0; i < columns; i++) {
    const a = grid[j][i], b = grid[j][i + 1], c = grid[j + 1][i], d = grid[j + 1][i + 1];
    if (noise(i + 17, j + 8) > .5) { topTriangle(terrain, a, b, d); topTriangle(terrain, a, d, c); line(a, d); }
    else { topTriangle(terrain, a, b, c); topTriangle(terrain, b, d, c); line(b, c); }
    line(a, b); line(a, c); if (j === rows - 1) line(c, d); if (i === columns - 1) line(b, d);
  }
  const terrainEdgeFloatCount = edges.length;
  function strip(target, points, width, owner, offset = .12) {
    const pairs = [];
    for (let i = 0; i < points.length; i++) {
      const p = points[i], a = points[Math.max(0, i - 1)], b = points[Math.min(points.length - 1, i + 1)], dx = b[0] - a[0], dz = b[1] - a[1], length = Math.hypot(dx, dz) || 1;
      const w = typeof width === 'function' ? width(i, points.length) : width;
      const left = [p[0] - dz / length * w, p[1] + dx / length * w], right = [p[0] + dz / length * w, p[1] - dx / length * w];
      pairs.push([[left[0], heightAt(left[0], left[1]) + offset, left[1]], [right[0], heightAt(right[0], right[1]) + offset, right[1]]]);
      if (i) { const previous = pairs[i - 1], current = pairs[i]; topTriangle(target, previous[0], previous[1], current[1], owner); topTriangle(target, previous[0], current[1], current[0], owner); }
    }
    return pairs;
  }
  for (const feature of riverFeatures) {
    const width = i => Math.max(.1, finite(feature.half_width, 1.5) + list(feature.width_waves).reduce((sum, wave) => sum + finite(wave.amplitude) * Math.sin(i * finite(wave.frequency)), 0));
    const banks = strip(river, feature.path, width, feature.owner, finite(feature.offset, .13));
    for (let i = 1; i < banks.length; i++) { line(banks[i - 1][0], banks[i][0], feature.owner); line(banks[i - 1][1], banks[i][1], feature.owner); }
  }
  for (const feature of routeFeatures) {
    feature.path = smoothPolyline(polygon(feature.points));
    strip(routes, feature.path, Math.max(.05, finite(feature.half_width, .4)), feature.owner, finite(feature.offset, .16));
  }
  function triangulate(poly) {
    let signed = 0; for (let i = 0; i < poly.length; i++) { const a = poly[i], b = poly[(i + 1) % poly.length]; signed += a[0] * b[1] - b[0] * a[1]; }
    const sign = signed > 0 ? 1 : -1, ids = poly.map((_, i) => i), out = [];
    const cross = (a, b, c) => (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]);
    const inside = (p, a, b, c) => {
      const c1 = ((b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])) * sign;
      const c2 = ((c[0] - b[0]) * (p[1] - b[1]) - (c[1] - b[1]) * (p[0] - b[0])) * sign;
      const c3 = ((a[0] - c[0]) * (p[1] - c[1]) - (a[1] - c[1]) * (p[0] - c[0])) * sign;
      return c1 >= -.0001 && c2 >= -.0001 && c3 >= -.0001;
    };
    let guard = poly.length * poly.length;
    while (ids.length > 3 && guard-- > 0) {
      let clipped = false;
      for (let i = 0; i < ids.length; i++) {
        const ia = ids[(i + ids.length - 1) % ids.length], ib = ids[i], ic = ids[(i + 1) % ids.length];
        if (cross(poly[ia], poly[ib], poly[ic]) * sign <= .00001) continue;
        if (ids.some(id => id !== ia && id !== ib && id !== ic && inside(poly[id], poly[ia], poly[ib], poly[ic]))) continue;
        out.push([ia, ib, ic]); ids.splice(i, 1); clipped = true; break;
      }
      if (!clipped) break;
    }
    if (ids.length === 3) out.push(ids.slice()); return out;
  }
  function building(poly, height, owner) {
    if (poly.length < 3) return;
    const base = poly.map(([x, z]) => [x, heightAt(x, z) - .04, z]), roofY = Math.max(...base.map(p => p[1])) + height, roof = poly.map(([x, z]) => [x, roofY, z]);
    for (const [a, b, c] of triangulate(poly)) topTriangle(solids, roof[a], roof[b], roof[c], owner);
    for (let i = 0; i < poly.length; i++) { const j = (i + 1) % poly.length; triangle(solids, base[i], base[j], roof[j], owner); triangle(solids, base[i], roof[j], roof[i], owner); line(roof[i], roof[j], owner); line(base[i], roof[i], owner); }
  }
  function transformed(cx, cz, coords, angle = 0) { const c = Math.cos(angle), s = Math.sin(angle); return coords.map(([x, z]) => [cx + x * c - z * s, cz + x * s + z * c]); }
  function rock(x, z, radius, height, owner, seed = 0) {
    const n = 5, base = [], cap = [];
    for (let i = 0; i < n; i++) {
      const a = i / n * Math.PI * 2, r = radius * (.75 + .35 * noise(i + seed, 19)), px = x + Math.cos(a) * r, pz = z + Math.sin(a) * r;
      base.push([px, heightAt(px, pz) - .06, pz]); cap.push([x + Math.cos(a) * r * .68, heightAt(x, z) + height * (.8 + .2 * noise(seed, i)), z + Math.sin(a) * r * .68]);
    }
    const center = [x, heightAt(x, z) + height, z];
    for (let i = 0; i < n; i++) { const j = (i + 1) % n; topTriangle(solids, center, cap[i], cap[j], owner); triangle(solids, base[i], base[j], cap[j], owner); triangle(solids, base[i], cap[j], cap[i], owner); line(cap[i], cap[j], owner); if (i % 2 === 0) line(base[i], cap[i], owner); }
  }
  function tree(x, z, radius, height, seed, owner) {
    const ground = heightAt(x, z), n = 5, ring = [], upper = [];
    for (let i = 0; i < n; i++) { const a = 2 * Math.PI * i / n + seed * .31; ring.push([x + Math.cos(a) * radius, ground + height * .30, z + Math.sin(a) * radius]); upper.push([x + Math.cos(a) * radius * .48, ground + height * .74, z + Math.sin(a) * radius * .48]); }
    const tip = [x + .13, ground + height, z - .12];
    for (let i = 0; i < n; i++) { const j = (i + 1) % n; triangle(solids, ring[i], ring[j], upper[j], owner); triangle(solids, ring[i], upper[j], upper[i], owner); triangle(solids, upper[i], upper[j], tip, owner); line(ring[i], ring[j], owner); if (i % 2 === 0) { line(ring[i], upper[i], owner); line(upper[i], tip, owner); } }
    line([x, ground, z], [x, ground + height * .32, z], owner);
  }
  for (const feature of resolveFeatures('buildings')) building(transformed(finite(feature.x), finite(feature.z), polygon(feature.footprint), finite(feature.angle)), Math.max(.1, finite(feature.height, 3)), feature.owner);
  for (const feature of resolveFeatures('rocks')) rock(finite(feature.x), finite(feature.z), Math.max(.1, finite(feature.radius, 1.4)), Math.max(.1, finite(feature.height, 1)), feature.owner, finite(feature.seed));
  for (const feature of resolveFeatures('trees')) tree(finite(feature.x), finite(feature.z), Math.max(.1, finite(feature.radius, 1.4)), Math.max(.1, finite(feature.height, 4.5)), finite(feature.seed), feature.owner);
  for (const site of fallbackSites) {
    const {x, z, id, renderId, symbol} = site;
    if (['forest', 'woods', 'grove'].includes(symbol)) {
      for (let i = 0; i < 7; i++) { const a = i * 2.399963, r = 2 + Math.sqrt(i) * 2.1; tree(x + Math.cos(a) * r, z + Math.sin(a) * r, 1.1 + noise(id, i) * .7, 3.8 + noise(i, id) * 2, i, renderId); }
    } else if (['cabin', 'house', 'building'].includes(symbol)) building(transformed(x, z, [[-2.8, -2.4], [1.8, -2.4], [3, -.8], [3, 2.5], [-2.8, 2.5]], -.12), 3.2, renderId);
    else if (['market', 'village', 'town'].includes(symbol)) {
      building(transformed(x - 3, z, [[-3.2, -2.8], [2, -2.8], [2, 0], [0, 0], [0, 2], [-3.2, 2]], .1), 3.6, renderId);
      building(transformed(x + 4, z - 2, [[-1.4, -2.8], [1.4, -2.8], [1.4, 2.8], [-1.4, 2.8]], -.2), 2.6, renderId);
    } else if (!['stream', 'river', 'water', 'path', 'road'].includes(symbol)) rock(x, z, 1.5, 1.2, renderId, id);
  }
  for (const site of sites) { site.y = heightAt(site.x, site.z); delete site.hasLayout; }
  const originId = positiveId(layout && layout.origin_scene);
  const originSite = originId ? sites.find(site => site.layoutId === originId) : null;
  const origin = originSite ? {x: originSite.x, z: originSite.z} : {x: 0, z: 0};
  const currentId = positiveId(input.currentId) || 0;
  const exploredBounds = sites.length ? {
    minX: Math.min(...sites.map(site => Math.min(site.x - site.radius, ...site.outline.map(p => p[0])))) - 8,
    maxX: Math.max(...sites.map(site => Math.max(site.x + site.radius, ...site.outline.map(p => p[0])))) + 8,
    minZ: Math.min(...sites.map(site => Math.min(site.z - site.radius, ...site.outline.map(p => p[1])))) - 8,
    maxZ: Math.max(...sites.map(site => Math.max(site.z + site.radius, ...site.outline.map(p => p[1])))) + 8
  } : {...bounds};
  return {
    terrain: new Float32Array(terrain), solids: new Float32Array(solids), edges: new Float32Array(edges), terrainEdgeFloatCount,
    river: new Float32Array(river), routes: new Float32Array(routes), sites, heightAt,
    riverPath: riverFeatures[0] && riverFeatures[0].path || [], routePoints: routeFeatures[0] && routeFeatures[0].path || [],
    bounds, exploredBounds, origin, currentId, renderIdToSiteId: [null, ...sites.map(site => site.id)],
    metersPerUnit: Math.max(.001, finite(layout && layout.meters_per_unit, 10)), coordinateLabel: '示意坐标'
  };
})();
