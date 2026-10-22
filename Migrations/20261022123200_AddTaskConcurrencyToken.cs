using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApplication1.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Токен конкурентности — системный столбец PostgreSQL xmin, он есть в каждой таблице.
    /// Отдельного DDL не требуется: миграция фиксирует только изменение модели.
    /// </remarks>
    public partial class AddTaskConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
