using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CentralAntifraude.Infrastructure.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class MensageriaEProjecoes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eventos_processados",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evento_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumidor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    tipo_do_evento = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    id_de_correlacao = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    processado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eventos_processados", x => x.id);
                    table.ForeignKey(
                        name: "fk_eventos_processados_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fila_de_mensagens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fila = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    corpo = table.Column<string>(type: "text", nullable: false),
                    disponivel_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recebimentos = table.Column<int>(type: "integer", nullable: false),
                    recibo = table.Column<Guid>(type: "uuid", nullable: true),
                    inserida_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fila_de_mensagens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mensagens_mortas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fila = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    corpo = table.Column<string>(type: "text", nullable: false),
                    recebimentos = table.Column<int>(type: "integer", nullable: false),
                    motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    inserida_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    movida_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mensagens_mortas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resumo_diario_de_decisoes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizacao_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dia = table.Column<DateOnly>(type: "date", nullable: false),
                    decisao = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quantidade = table.Column<int>(type: "integer", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resumo_diario_de_decisoes", x => x.id);
                    table.ForeignKey(
                        name: "fk_resumo_diario_de_decisoes_organizacoes_organizacao_id",
                        column: x => x.organizacao_id,
                        principalTable: "organizacoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eventos_processados_consumidor_evento",
                table: "eventos_processados",
                columns: new[] { "consumidor", "evento_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eventos_processados_organizacao_id",
                table: "eventos_processados",
                column: "organizacao_id");

            migrationBuilder.CreateIndex(
                name: "ix_fila_de_mensagens_fila_disponivel_em",
                table: "fila_de_mensagens",
                columns: new[] { "fila", "disponivel_em" });

            migrationBuilder.CreateIndex(
                name: "ix_fila_de_mensagens_recibo",
                table: "fila_de_mensagens",
                column: "recibo",
                unique: true,
                filter: "recibo IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mensagens_mortas_fila_movida_em",
                table: "mensagens_mortas",
                columns: new[] { "fila", "movida_em" });

            migrationBuilder.CreateIndex(
                name: "ix_resumo_diario_organizacao_dia_decisao",
                table: "resumo_diario_de_decisoes",
                columns: new[] { "organizacao_id", "dia", "decisao" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eventos_processados");

            migrationBuilder.DropTable(
                name: "fila_de_mensagens");

            migrationBuilder.DropTable(
                name: "mensagens_mortas");

            migrationBuilder.DropTable(
                name: "resumo_diario_de_decisoes");
        }
    }
}
