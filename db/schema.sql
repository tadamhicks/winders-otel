-- BooksDb schema + seed data for books-dotnet. Idempotent; run with:
--   sqlcmd -S .\SQLEXPRESS -E -C -i db\schema.sql -v AppPoolLogin="IIS APPPOOL\books-dotnet"
IF DB_ID('BooksDb') IS NULL
    CREATE DATABASE BooksDb;
GO
USE BooksDb;
GO

IF OBJECT_ID('dbo.Books') IS NULL
BEGIN
    CREATE TABLE dbo.Books (
        id          INT IDENTITY(1,1) PRIMARY KEY,
        title       NVARCHAR(255)  NOT NULL,
        author      NVARCHAR(255)  NOT NULL,
        isbn        NVARCHAR(20)   NOT NULL CONSTRAINT UQ_Books_isbn UNIQUE,
        description NVARCHAR(MAX)  NULL,
        price       DECIMAL(10,2)  NOT NULL,
        created_at  DATETIME2(3)   NOT NULL CONSTRAINT DF_Books_created_at DEFAULT SYSUTCDATETIME(),
        updated_at  DATETIME2(3)   NULL
    );

    INSERT INTO dbo.Books (title, author, isbn, description, price) VALUES
        (N'The Pragmatic Programmer', N'David Thomas, Andrew Hunt', N'978-0135957059', N'Your journey to mastery.', 49.99),
        (N'Clean Code', N'Robert C. Martin', N'978-0132350884', N'A handbook of agile software craftsmanship.', 37.99),
        (N'Designing Data-Intensive Applications', N'Martin Kleppmann', N'978-1449373320', N'The big ideas behind reliable, scalable, and maintainable systems.', 45.99),
        (N'Site Reliability Engineering', N'Betsy Beyer et al.', N'978-1491929124', N'How Google runs production systems.', 39.99),
        (N'Observability Engineering', N'Charity Majors, Liz Fong-Jones, George Miranda', N'978-1492076445', N'Achieving production excellence.', 54.99);
END
GO

-- Windows login for the IIS app pool identity (no stored password).
IF SUSER_ID(N'$(AppPoolLogin)') IS NULL
    CREATE LOGIN [$(AppPoolLogin)] FROM WINDOWS;
IF USER_ID(N'$(AppPoolLogin)') IS NULL
    CREATE USER [$(AppPoolLogin)] FOR LOGIN [$(AppPoolLogin)];
ALTER ROLE db_datareader ADD MEMBER [$(AppPoolLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppPoolLogin)];
GO
