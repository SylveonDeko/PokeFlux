#!/usr/bin/env bash
# Uploads the images in EeveeCore/data to the CDN under the names MediaUrls expects.
# Usage: tools/upload-cdn.sh <key> <user@host:/remote/dir>
set -euo pipefail
key=$1
dest=$2
root="$(cd "$(dirname "$0")/../EeveeCore" && pwd)"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

cd "$root"
find -L data -type f \( -iname '*.png' -o -iname '*.gif' -o -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.webp' \) -print0 |
  while IFS= read -r -d '' file; do
    rel=${file#./}
    name=$(printf '%s' "$rel" | openssl dgst -sha256 -hmac "$key" -r | cut -c1-32)
    ext=$(printf '%s' "${rel##*.}" | tr '[:upper:]' '[:lower:]')
    ln -sf "$root/$rel" "$stage/$name.$ext"
  done

echo "Uploading $(find "$stage" -type l | wc -l) images"
rsync -rLt --ignore-existing --chmod=F644,D755 "$stage/" "$dest/"
