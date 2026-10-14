"""Write small procedural .vrma clips (VRM Animation) for testing the animation library.

Usage: python -I make_vrma.py <rig.vrma> <out folder>

<rig.vrma> supplies the humanoid skeleton in T-pose (three-vrm's examples/models/test.vrma works). Each clip below is
a list of keys per bone; a key is a rotation in *world* space (axis, degrees) applied to the bone's rest pose, so clips
are written as "rotate the left upper arm 68 degrees down" instead of in the rig's local axes. glTF axes: +Y up,
+Z the way the character faces, +X the character's left.
Only the standard library is used.
"""
import json
import math
import os
import struct
import sys


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qinv(q):
    x, y, z, w = q
    return (-x, -y, -z, w)


def qaxis(axis, deg):
    x, y, z = axis
    n = math.sqrt(x * x + y * y + z * z)
    s = math.sin(math.radians(deg) / 2)
    return (x / n * s, y / n * s, z / n * s, math.cos(math.radians(deg) / 2))


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q))
    return tuple(c / n for c in q)


IDENT = (0.0, 0.0, 0.0, 1.0)
UP, FWD, LEFT = (0, 1, 0), (0, 0, 1), (1, 0, 0)


def world(*rots):
    """Compose world-space rotations, applied in order."""
    q = IDENT
    for axis, deg in rots:
        q = qmul(qaxis(axis, deg), q)
    return q


def arms_down(extra_left=(), extra_right=()):
    # Left arm points +X; rotating about +Z by -deg swings it down. Right arm points -X; +deg swings it down.
    return {
        "leftUpperArm": world((FWD, -68), *extra_left),
        "rightUpperArm": world((FWD, 68), *extra_right),
        "leftLowerArm": world((UP, 12)),
        "rightLowerArm": world((UP, -12)),
    }


def sway(t, length):
    p = 2 * math.pi * t / length
    keys = arms_down()
    keys["spine"] = world((FWD, 2.5 * math.sin(p)))
    keys["chest"] = world((LEFT, -1.5 * math.sin(2 * p)), (FWD, 1.5 * math.sin(p)))  # breathing + sway
    keys["head"] = world((FWD, -2.0 * math.sin(p)), (LEFT, 2.0))
    keys["hips"] = world((UP, 3.0 * math.sin(p)))
    return keys


def look_around(t, length):
    p = 2 * math.pi * t / length
    keys = arms_down()
    yaw = 28 * math.sin(p) * min(1.0, abs(math.sin(p)) * 2)  # linger at each side
    keys["neck"] = world((UP, yaw * 0.4))
    keys["head"] = world((UP, yaw * 0.6), (LEFT, -4 + 3 * math.sin(2 * p)))
    keys["chest"] = world((UP, yaw * 0.15), (LEFT, -1.0 * math.sin(2 * p)))
    return keys


def wave(t, length):
    # Raise the right arm (pointing -X) up to the side, bend the forearm up, wag it.
    raise_amount = min(1.0, t / 0.4, (length - t) / 0.4)
    wag = 22 * math.sin(2 * math.pi * t / 0.55) * raise_amount
    keys = arms_down()
    keys["rightUpperArm"] = world((FWD, 68 - 68 * raise_amount - 35 * raise_amount))  # down → out → up a little
    keys["rightLowerArm"] = world((FWD, -95 * raise_amount), (FWD, wag))
    keys["rightHand"] = world((FWD, 0.3 * wag))
    keys["head"] = world((FWD, -4 * raise_amount))
    return keys


CLIPS = {
    "Relaxed Sway": (sway, 6.0, {"category": "idle", "loop": True, "idleWeight": 2, "tags": ["calm"]}),
    "Look Around": (look_around, 8.0, {"category": "idle", "loop": True, "idleWeight": 1, "tags": ["curious"]}),
    "Wave": (wave, 2.6, {"category": "gesture", "loop": False, "blendIn": 0.25, "blendOut": 0.35, "tags": ["greeting", "wave"]}),
}


def read_glb(path):
    b = open(path, "rb").read()
    jlen = struct.unpack("<I", b[12:16])[0]
    return json.loads(b[20:20 + jlen])


def node_world(nodes, parents, i):
    q = tuple(nodes[i].get("rotation", IDENT))
    p = parents.get(i)
    while p is not None:
        q = qmul(tuple(nodes[p].get("rotation", IDENT)), q)
        p = parents.get(p)
    return q


def write_clip(rig, name, fn, length, fps=30):
    gltf = json.loads(json.dumps(rig))
    bones = gltf["extensions"]["VRMC_vrm_animation"]["humanoid"]["humanBones"]
    keep = bones["hips"]["node"]
    gltf["scenes"] = [{"nodes": [keep]}]
    ext = gltf["extensions"]["VRMC_vrm_animation"]
    ext.pop("expressions", None)
    ext.pop("lookAt", None)
    nodes = gltf["nodes"]
    parents = {c: i for i, n in enumerate(nodes) for c in n.get("children", [])}

    frames = int(round(length * fps)) + 1
    times = [min(length, f / fps) for f in range(frames)]
    samples = [fn(t, length) for t in times]
    animated = sorted({b for s in samples for b in s})

    blob = bytearray()
    accessors, views, channels, samplers = [], [], [], []

    def add(data, fmt, count, kind, mn=None, mx=None):
        while len(blob) % 4:
            blob.append(0)
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)})
        blob.extend(data)
        acc = {"bufferView": len(views) - 1, "componentType": 5126, "count": count, "type": kind}
        if mn is not None:
            acc["min"], acc["max"] = mn, mx
        accessors.append(acc)
        return len(accessors) - 1

    t_acc = add(struct.pack("<%df" % frames, *times), "f", frames, "SCALAR", [0.0], [length])
    for bone in animated:
        i = bones[bone]["node"]
        rest_local = tuple(nodes[i].get("rotation", IDENT))
        parent_world = node_world(nodes, parents, parents[i]) if i in parents else IDENT
        out = []
        prev = None
        for s in samples:
            delta = s.get(bone, IDENT)
            # world_new = delta * world_rest  =>  local_new = parentWorld^-1 * delta * parentWorld * local_rest
            q = qnorm(qmul(qmul(qmul(qinv(parent_world), delta), parent_world), rest_local))
            if prev is not None and sum(a * b for a, b in zip(prev, q)) < 0:
                q = tuple(-c for c in q)  # keep neighbouring keys in the same hemisphere
            prev = q
            out.extend(q)
        o_acc = add(struct.pack("<%df" % len(out), *out), "f", frames, "VEC4")
        samplers.append({"input": t_acc, "output": o_acc, "interpolation": "LINEAR"})
        channels.append({"sampler": len(samplers) - 1, "target": {"node": i, "path": "rotation"}})

    gltf["accessors"], gltf["bufferViews"] = accessors, views
    gltf["buffers"] = [{"byteLength": len(blob)}]
    gltf["animations"] = [{"name": name, "channels": channels, "samplers": samplers}]
    gltf["asset"] = {"version": "2.0", "generator": "Mate Engine QoL make_vrma.py"}

    js = json.dumps(gltf, separators=(",", ":")).encode()
    js += b" " * ((4 - len(js) % 4) % 4)
    while len(blob) % 4:
        blob.append(0)
    total = 12 + 8 + len(js) + 8 + len(blob)
    return (struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(js), 0x4E4F534A) + js
            + struct.pack("<II", len(blob), 0x004E4942) + bytes(blob))


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    rig = read_glb(sys.argv[1])
    out = sys.argv[2]
    os.makedirs(out, exist_ok=True)
    for name, (fn, length, sidecar) in CLIPS.items():
        with open(os.path.join(out, name + ".vrma"), "wb") as f:
            f.write(write_clip(rig, name, fn, length))
        with open(os.path.join(out, name + ".json"), "w", encoding="utf-8") as f:
            json.dump(dict({"name": name}, **sidecar), f, indent=2)
        print("wrote", name)


if __name__ == "__main__":
    main()
