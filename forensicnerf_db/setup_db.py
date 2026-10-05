import sqlite3
from pathlib import Path

DB_PATH = Path("forensicnerf.db")
SCHEMA_PATH = Path("schema.sql")

def init_db():
    conn = sqlite3.connect(DB_PATH)
    conn.executescript(SCHEMA_PATH.read_text(encoding="utf-8-sig"))
    conn.commit()
    return conn

def smoke_test(conn):
    cur = conn.cursor()
    cur.execute("INSERT INTO case_record VALUES (?,?,?,?,?)",
                ("CASE-001", "INV-01", "2026-10-05T09:00:00", "abc123", 1))
    cur.execute("INSERT INTO export_package VALUES (?,?,?,?,?)",
                ("PKG-001", "CASE-001", "scene.mp4", "imu.csv", "abc123"))
    cur.execute("INSERT INTO processing_job VALUES (?,?,?,?,?)",
                ("JOB-001", "PKG-001", "PENDING", None, None))
    conn.commit()

    cur.execute("""
        SELECT c.case_id, j.job_id, j.status
        FROM processing_job j
        JOIN export_package p ON j.package_id = p.package_id
        JOIN case_record c ON p.case_id = c.case_id
    """)
    print("Joined row:", cur.fetchone())

if __name__ == "__main__":
    conn = init_db()
    smoke_test(conn)
    conn.close()
    print("DB created and tested OK ->", DB_PATH.resolve())
