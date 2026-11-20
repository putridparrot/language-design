-- TrackerDb migration 0001-baseline, for sqlite: written by `piper db migrate` from TrackerDb.migrations/0001-baseline.json.
-- Review it before it's applied; once it has been, leave it as it is (a change is a new migration).

CREATE TABLE "Comments" (
    "id" INTEGER NOT NULL CONSTRAINT "PK_Comments" PRIMARY KEY,
    "issueId" INTEGER NOT NULL,
    "author" TEXT NOT NULL,
    "text" TEXT NOT NULL,
    "created" TEXT NOT NULL
);
GO

CREATE TABLE "Issues" (
    "id" INTEGER NOT NULL CONSTRAINT "PK_Issues" PRIMARY KEY,
    "projectId" INTEGER NOT NULL,
    "title" TEXT NOT NULL,
    "description" TEXT NOT NULL,
    "priority" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "assignee" TEXT NULL,
    "resolution" TEXT NULL,
    "created" TEXT NOT NULL,
    "updated" TEXT NOT NULL
);
GO

CREATE TABLE "PiperIdempotency" (
    "Key" TEXT NOT NULL CONSTRAINT "PK_PiperIdempotency" PRIMARY KEY,
    "Fingerprint" TEXT NOT NULL,
    "Until" TEXT NOT NULL,
    "Status" INTEGER NULL,
    "ContentType" TEXT NULL,
    "Location" TEXT NULL,
    "Body" BLOB NULL
);
GO

CREATE TABLE "PiperOutbox" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_PiperOutbox" PRIMARY KEY,
    "Destination" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "Body" TEXT NOT NULL,
    "Key" TEXT NULL,
    "NotBefore" TEXT NULL,
    "Created" TEXT NOT NULL,
    "Claimant" TEXT NULL,
    "ClaimedUntil" TEXT NULL,
    "Sent" TEXT NULL
);
GO

CREATE TABLE "Projects" (
    "id" INTEGER NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY,
    "code" TEXT NOT NULL,
    "name" TEXT NOT NULL
);
GO

CREATE INDEX "IX_Comments_issueId" ON "Comments" ("issueId");
GO

CREATE INDEX "IX_Issues_projectId" ON "Issues" ("projectId");
GO

CREATE INDEX "IX_Issues_projectId_status" ON "Issues" ("projectId", "status");
GO

CREATE INDEX "IX_PiperIdempotency_Until" ON "PiperIdempotency" ("Until");
GO

CREATE INDEX "IX_PiperOutbox_Sent_Created" ON "PiperOutbox" ("Sent", "Created");
GO

CREATE INDEX "IX_Projects_code" ON "Projects" ("code");
GO

