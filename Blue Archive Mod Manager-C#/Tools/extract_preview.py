import sys, os, json
from pathlib import Path

try:
    import UnityPy
except ImportError:
    print(json.dumps({"error": "UnityPy not installed"}))
    sys.exit(1)

def extract(bundle_path, out_dir, max_textures=6):
    env = UnityPy.load(bundle_path)
    textures = []
    for obj in env.objects:
        if obj.type.name == "Texture2D":
            data = obj.read()
            name = getattr(data, "m_Name", None) or f"tex_{obj.path_id}"
            # Sanitize filename
            name = "".join(c if c.isalnum() or c in "._-" else "_" for c in name)
            img = data.image
            if img is None:
                continue
            w, h = img.size
            if w * h < 64 * 64:
                continue
            out_path = Path(out_dir) / f"{name}.png"
            img.save(out_path)
            textures.append({
                "path": str(out_path),
                "name": name,
                "width": w,
                "height": h,
                "pixels": w * h
            })
    textures.sort(key=lambda x: x["pixels"], reverse=True)
    return textures[:max_textures]

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(json.dumps({"error": "Usage: extract_preview.py <bundle> <out_dir>"}))
        sys.exit(1)
    result = extract(sys.argv[1], sys.argv[2])
    print(json.dumps({"textures": result}))
