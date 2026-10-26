"""Generate SonosCast UI art through the ComfyUI web API (Z-Image Turbo, optional BiRefNet cutout).

usage: python comfy_gen.py <out.png> <width> <height> <seed> [--cutout] <prompt...>
"""
import json, sys, time, urllib.request, urllib.parse, uuid

API = "http://127.0.0.1:8188"


def post(path, data):
    req = urllib.request.Request(API + path, json.dumps(data).encode(), {"Content-Type": "application/json"})
    return json.load(urllib.request.urlopen(req))


def workflow(prompt, width, height, seed, cutout):
    wf = {
        "unet": {"class_type": "UNETLoader", "inputs": {"unet_name": "z_image_turbo_bf16.safetensors", "weight_dtype": "default"}},
        "clip": {"class_type": "CLIPLoader", "inputs": {"clip_name": "qwen_3_4b.safetensors", "type": "lumina2"}},
        "vae": {"class_type": "VAELoader", "inputs": {"vae_name": "ae.safetensors"}},
        "shift": {"class_type": "ModelSamplingAuraFlow", "inputs": {"model": ["unet", 0], "shift": 3.0}},
        "pos": {"class_type": "CLIPTextEncode", "inputs": {"clip": ["clip", 0], "text": prompt}},
        "neg": {"class_type": "ConditioningZeroOut", "inputs": {"conditioning": ["pos", 0]}},
        "latent": {"class_type": "EmptySD3LatentImage", "inputs": {"width": width, "height": height, "batch_size": 1}},
        "sample": {"class_type": "KSampler", "inputs": {
            "model": ["shift", 0], "positive": ["pos", 0], "negative": ["neg", 0], "latent_image": ["latent", 0],
            "seed": seed, "steps": 9, "cfg": 1.0, "sampler_name": "res_multistep", "scheduler": "simple", "denoise": 1.0}},
        "decode": {"class_type": "VAEDecode", "inputs": {"samples": ["sample", 0], "vae": ["vae", 0]}},
    }
    image = ["decode", 0]
    if cutout:
        wf["bgm"] = {"class_type": "LoadBackgroundRemovalModel", "inputs": {"bg_removal_name": "birefnet.safetensors"}}
        wf["mask"] = {"class_type": "RemoveBackground", "inputs": {"bg_removal_model": ["bgm", 0], "image": image}}
        # JoinImageWithAlpha treats the mask as "what to hide", so invert the foreground mask first.
        wf["inv"] = {"class_type": "InvertMask", "inputs": {"mask": ["mask", 0]}}
        wf["rgba"] = {"class_type": "JoinImageWithAlpha", "inputs": {"image": image, "alpha": ["inv", 0]}}
        image = ["rgba", 0]
    wf["save"] = {"class_type": "SaveImage", "inputs": {"images": image, "filename_prefix": "sonoscast"}}
    return wf


def main():
    out, width, height, seed = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
    rest = sys.argv[5:]
    cutout = "--cutout" in rest
    prompt = " ".join(a for a in rest if a != "--cutout")

    pid = post("/prompt", {"prompt": workflow(prompt, width, height, seed, cutout), "client_id": str(uuid.uuid4())})["prompt_id"]
    while True:
        hist = json.load(urllib.request.urlopen(f"{API}/history/{pid}"))
        if pid in hist:
            entry = hist[pid]
            if entry.get("status", {}).get("status_str") == "error":
                sys.exit("ComfyUI error: " + json.dumps(entry["status"])[:2000])
            img = entry["outputs"]["save"]["images"][0]
            break
        time.sleep(1)
    q = urllib.parse.urlencode({"filename": img["filename"], "subfolder": img["subfolder"], "type": img["type"]})
    with open(out, "wb") as f:
        f.write(urllib.request.urlopen(f"{API}/view?{q}").read())
    print("saved", out)


if __name__ == "__main__":
    main()
