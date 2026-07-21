# Resources

## `yerevan-districts.geojson`

Static, versioned boundary polygons for Yerevan's 12 administrative districts (`admin_level=5`
relations in OSM — despite the name, this is the level OSM uses for Yerevan's districts; levels
8/9/10 are empty for this area). Used for local point-in-polygon district assignment (see
`IDistrictBoundaryProvider` / `DistrictBoundaryProvider` in `Services/`) — **no geocoder in the
correctness path**.

- **Source**: OpenStreetMap, fetched via the Overpass API (`overpass-api.de/api/interpreter`) on
  **2026-07-21**. Query: all `boundary=administrative` relations inside the Yerevan `admin_level=4`
  area, filtered to the 12 known district names; full ring geometry then fetched by relation id
  (`out geom`).
- **License**: © OpenStreetMap contributors, [ODbL 1.0](https://opendatacommons.org/licenses/odbl/).
  Wherever this data (or a district assignment derived from it) is rendered on a map, the
  attribution **"© OpenStreetMap contributors"** must appear in the map's attribution control,
  per ODbL's attribution requirement. The FeatureCollection itself carries top-level
  `attribution` / `license` / `sourceNote` members recording this.
- **Geometry**: WGS84, `[longitude, latitude]` per the GeoJSON spec. Coordinates are the
  **unsimplified** original OSM way geometry — the file is only ~74 KB (well under the ~1 MB
  budget) at full resolution, so no simplification was applied. This is a deliberate choice:
  simplifying each district's ring independently risks the classic gap/overlap bug at shared
  borders (two adjacent districts referencing the same OSM way could pick different simplified
  vertices along it). Keeping the exact source coordinates guarantees shared borders stay
  byte-identical.
- **Properties per feature**: `code` (stable kebab-case slug), `nameEn`, `nameHy` (Armenian),
  `nameRu` (Russian), `osmRelationId`.
- **Verification performed when building this asset**: exactly 12 features, no duplicate codes;
  zero point-in-polygon overlaps and no shared-border gaps found across a ~10k-point grid sample
  of the city; four reference points (Republic Square/Kentron, and one point each in
  Malatia-Sebastia, Arabkir, Nor Nork) independently cross-checked against Nominatim reverse
  geocoding (`nominatim.openstreetmap.org/reverse`), not just against this file's own polygons.
- **Malatia-Sebastia is a `MultiPolygon`** (two outer rings) — this reflects the source OSM
  relation and is preserved as-is; all other 11 districts are single `Polygon` geometries.

If this file is ever regenerated, re-run the same verification (feature count, no-overlap grid
sample, independent reverse-geocode spot checks) before committing — see the git history of this
file for the process.
