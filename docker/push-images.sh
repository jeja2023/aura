#!/usr/bin/env sh
# File: Docker Push Images Script

set -eu

API_IMAGE_REPO="${API_IMAGE_REPO:-aura-api}"
AI_IMAGE_REPO="${AI_IMAGE_REPO:-aura-ai}"
MEDIA_PROVIDER_SIMULATOR_IMAGE_REPO="${MEDIA_PROVIDER_SIMULATOR_IMAGE_REPO:-aura-media-provider-simulator}"
IMAGE_TAG="${IMAGE_TAG:-}"

if [ -z "$IMAGE_TAG" ]; then
  echo "Set IMAGE_TAG first."
  exit 1
fi

API_IMAGE="${API_IMAGE_REPO}:${IMAGE_TAG}"
AI_IMAGE="${AI_IMAGE_REPO}:${IMAGE_TAG}"
SIMULATOR_IMAGE="${MEDIA_PROVIDER_SIMULATOR_IMAGE_REPO}:${IMAGE_TAG}"

echo "Pushing image: $API_IMAGE"
docker push "$API_IMAGE"

echo "Pushing image: $AI_IMAGE"
docker push "$AI_IMAGE"

echo "Pushing image: $SIMULATOR_IMAGE"
docker push "$SIMULATOR_IMAGE"

echo "[RESULT] Images pushed."
