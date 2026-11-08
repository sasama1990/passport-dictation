import { DatabaseSync } from "node:sqlite";
import { readdirSync, readFileSync } from "node:fs";

// Local development adapter. Production uses the hosted D1 binding.
export function openDatabase(filename = ":memory:") {
  const database = new DatabaseSync(filename);
  database.exec("PRAGMA foreign_keys = ON");
  database.exec("CREATE TABLE IF NOT EXISTS relay_migrations (name TEXT PRIMARY KEY)");
  const directory = new URL("./drizzle/", import.meta.url);
  for (const name of readdirSync(directory).filter(name => name.endsWith(".sql")).sort()) {
    if (database.prepare("SELECT name FROM relay_migrations WHERE name=?").get(name)) continue;
    database.exec("BEGIN");
    try {
      database.exec(readFileSync(new URL(name, directory), "utf8"));
      database.prepare("INSERT INTO relay_migrations VALUES (?)").run(name);
      database.exec("COMMIT");
    } catch (error) {
      database.exec("ROLLBACK");
      throw error;
    }
  }
  function prepared(sql, values = []) {
    return {
      bind(...bindings) { return prepared(sql, bindings); },
      async first() { return database.prepare(sql).get(...values) || null; },
      async all() { return { results: database.prepare(sql).all(...values), success: true }; },
      execute() {
        const result = database.prepare(sql).run(...values);
        return { success: true, meta: { changes: Number(result.changes) } };
      },
      async run() { return this.execute(); },
    };
  }
  return {
    raw: database,
    prepare: prepared,
    async batch(statements) {
      database.exec("BEGIN");
      try {
        const results = [];
        for (const statement of statements) results.push(statement.execute());
        database.exec("COMMIT");
        return results;
      } catch (error) {
        database.exec("ROLLBACK");
        throw error;
      }
    },
    close() { database.close(); },
  };
}
