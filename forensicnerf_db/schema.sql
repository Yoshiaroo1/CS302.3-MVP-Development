
/*
below notes of SQL database for documentation 

case_record -- 

case_id is the primary key, has a unique ID such as CASE-001
investigator_id records who captures the scene
timestamp records when the case was logged and stored as ISO 8601 text string
checksum hashes the original capture data from the phone at the time
checksum_verified is either 0 or 1. implements the 'checksum matches?' decision after hash is recomputed

export_package --

package_id is the primary key
case_id links to the case_record
video_path and imu_log_path point to video file and sensor log
checksum is the hash of this specific package

processing_job --

job_id is the primary key
package_id links to the export package being processed
status is restricted by CHECK constraint to PENDING, RUNNING, SUCCESS or FAILED
start_time and end_time are nullable

reconstruction_output --

output_id is the primary key
job_id links to the job that produced it
splat_path is the Gaussian Splatting file loaded in Unity
mesh_path is the optional exported mesh, nullable due to not every job producing one
*/


PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS case_record (
    case_id          TEXT PRIMARY KEY,
    investigator_id  TEXT NOT NULL,
    timestamp        TEXT NOT NULL,
    checksum         TEXT NOT NULL,
    checksum_verified INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS export_package (
    package_id   TEXT PRIMARY KEY,
    case_id      TEXT NOT NULL,
    video_path   TEXT NOT NULL,
    imu_log_path TEXT NOT NULL,
    checksum     TEXT NOT NULL,
    FOREIGN KEY (case_id) REFERENCES case_record(case_id)
);

CREATE TABLE IF NOT EXISTS processing_job (
    job_id      TEXT PRIMARY KEY,
    package_id  TEXT NOT NULL,
    status      TEXT NOT NULL CHECK (status IN ('PENDING','RUNNING','SUCCESS','FAILED')),
    start_time  TEXT,
    end_time    TEXT,
    FOREIGN KEY (package_id) REFERENCES export_package(package_id)
);

CREATE TABLE IF NOT EXISTS reconstruction_output (
    output_id   TEXT PRIMARY KEY,
    job_id      TEXT NOT NULL,
    splat_path  TEXT NOT NULL,
    mesh_path   TEXT,
    FOREIGN KEY (job_id) REFERENCES processing_job(job_id)
);
