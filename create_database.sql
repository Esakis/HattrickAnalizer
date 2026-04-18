-- Uruchom ten skrypt w SQL Server Management Studio
-- Serwer: DESKTOP-UMO1TMH\SQLEXPRESS

IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'HattrickAnalizer')
    CREATE DATABASE HattrickAnalizer;
GO

USE HattrickAnalizer;
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PlayerSkillHistory')
CREATE TABLE PlayerSkillHistory (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    PlayerId      INT   NOT NULL,
    TeamId        INT   NOT NULL,
    RecordedDate  DATE  NOT NULL,
    -- Umiejętności
    Keeper        INT   NOT NULL DEFAULT 0,
    Defending     INT   NOT NULL DEFAULT 0,
    Playmaking    INT   NOT NULL DEFAULT 0,
    Winger        INT   NOT NULL DEFAULT 0,
    Passing       INT   NOT NULL DEFAULT 0,
    Scoring       INT   NOT NULL DEFAULT 0,
    SetPieces     INT   NOT NULL DEFAULT 0,
    -- Podstawowe
    Form          INT   NOT NULL DEFAULT 0,
    Stamina       INT   NOT NULL DEFAULT 0,
    Age           INT   NOT NULL DEFAULT 0,
    TSI           INT   NOT NULL DEFAULT 0,
    Experience    INT   NOT NULL DEFAULT 0,
    Loyalty       INT   NOT NULL DEFAULT 0,
    Leadership    INT   NOT NULL DEFAULT 0,
    InjuryLevel   INT   NOT NULL DEFAULT 0,
    -- Statystyki meczowe
    TotalMatches  INT   NOT NULL DEFAULT 0,
    Goals         INT   NOT NULL DEFAULT 0,
    Assists       INT   NOT NULL DEFAULT 0,
    YellowCards   INT   NOT NULL DEFAULT 0,
    RedCards      INT   NOT NULL DEFAULT 0,
    AverageRating FLOAT NOT NULL DEFAULT 0,
    AverageForm   FLOAT NOT NULL DEFAULT 0,
    MinutesPlayed INT   NOT NULL DEFAULT 0
);
GO

PRINT 'Baza HattrickAnalizer gotowa.';
