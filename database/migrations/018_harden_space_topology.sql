-- Harden the relational source of truth for graph projection.

ALTER TABLE space_topology_edge
  ADD COLUMN IF NOT EXISTS enabled BOOLEAN NOT NULL DEFAULT TRUE,
  ADD COLUMN IF NOT EXISTS valid_from TIMESTAMPTZ NULL,
  ADD COLUMN IF NOT EXISTS valid_to TIMESTAMPTZ NULL,
  ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP;

DO $$
BEGIN
  IF EXISTS (
    SELECT 1
    FROM space_topology_edge
    GROUP BY from_camera_id, to_camera_id, relation_type
    HAVING COUNT(*) > 1
  ) THEN
    RAISE EXCEPTION 'space_topology_edge contains duplicate edges; resolve duplicates before migration 018';
  END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_space_topology_edge_relation
  ON space_topology_edge(from_camera_id, to_camera_id, relation_type);

CREATE INDEX IF NOT EXISTS idx_space_topology_to_camera
  ON space_topology_edge(to_camera_id, from_camera_id)
  WHERE enabled = TRUE;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_space_topology_from_camera') THEN
    ALTER TABLE space_topology_edge
      ADD CONSTRAINT fk_space_topology_from_camera
      FOREIGN KEY(from_camera_id) REFERENCES map_camera(camera_id) ON DELETE CASCADE NOT VALID;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_space_topology_to_camera') THEN
    ALTER TABLE space_topology_edge
      ADD CONSTRAINT fk_space_topology_to_camera
      FOREIGN KEY(to_camera_id) REFERENCES map_camera(camera_id) ON DELETE CASCADE NOT VALID;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_space_topology_distinct_cameras') THEN
    ALTER TABLE space_topology_edge
      ADD CONSTRAINT ck_space_topology_distinct_cameras CHECK (from_camera_id <> to_camera_id) NOT VALID;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_space_topology_valid_range') THEN
    ALTER TABLE space_topology_edge
      ADD CONSTRAINT ck_space_topology_valid_range CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to > valid_from) NOT VALID;
  END IF;
END $$;
