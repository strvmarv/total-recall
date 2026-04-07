-- spike/dotnet/src/TotalRecall.Spike/Storage/Schema.sql
CREATE TABLE IF NOT EXISTS entries (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    content TEXT NOT NULL,
    created INTEGER NOT NULL
);

CREATE VIRTUAL TABLE IF NOT EXISTS vec_entries USING vec0(
    id        INTEGER PRIMARY KEY,
    embedding FLOAT[384]
);
