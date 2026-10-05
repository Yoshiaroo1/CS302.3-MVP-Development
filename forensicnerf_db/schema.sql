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
