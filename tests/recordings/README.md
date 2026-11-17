# Recorded laps

Four laps recorded with SWGR Wheel Bridge 0.1, one for each vehicle class. They are copied from that repository's
`Dumps/recordings` at commit `fb3e573a1c` (MIT licence, see `../../LICENSE`) and gzipped with `gzip -9 -n`. They are not
edited.

| File | Vehicle |
| --- | --- |
| `telemetry_20261013_021906.tsv.gz` | Car (land speeder) |
| `telemetry_20261013_174533.tsv.gz` | Bike (speeder bike) |
| `telemetry_20261016_191733.tsv.gz` | Skimmer (skim speeder) |
| `telemetry_20261016_192125.tsv.gz` | Pod (podracer) |

The tests replay them through the force model. Each recording carries the game's own `AngVelZ`, which Game Link Wheel
never receives. The tests replace it with the yaw rate worked out from the heading (`Vnsg.YawRate`), so the model runs
on the same input it gets in use. The Load tuning of the shipped profiles was carried over on these laps
(`changes/0349`); the test `ProfileLoadTests.LoadRetune` checks that tuning.
