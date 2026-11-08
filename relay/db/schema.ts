import { sqliteTable, text, integer, index, uniqueIndex } from "drizzle-orm/sqlite-core";

export const rooms = sqliteTable("rooms", {
  id: text("id").primaryKey(),
  receiverHash: text("receiver_hash").notNull(),
  deviceHash: text("device_hash"),
  deviceId: text("device_id"),
  pairCode: text("pair_code").notNull(),
  pairNonce: text("pair_nonce").notNull(),
  pairExpires: integer("pair_expires").notNull(),
  lastSeen: integer("last_seen").notNull(),
  cleanupAt: integer("cleanup_at").notNull(),
  createdAt: integer("created_at").notNull(),
}, table => [uniqueIndex("rooms_pair_code").on(table.pairCode)]);

export const commands = sqliteTable("commands", {
  key: text("key").primaryKey(),
  roomId: text("room_id").notNull().references(() => rooms.id, { onDelete: "cascade" }),
  id: text("id").notNull(),
  action: text("action").notNull(),
  text: text("text").notNull(),
  payloadHash: text("payload_hash").notNull(),
  createdAt: integer("created_at").notNull(),
  expiresAt: integer("expires_at").notNull(),
  resultStatus: text("result_status"),
  resultCode: integer("result_code"),
}, table => [index("commands_pending").on(table.roomId, table.expiresAt)]);

export const rateLimits = sqliteTable("rate_limits", {
  key: text("key").primaryKey(),
  count: integer("count").notNull(),
  expiresAt: integer("expires_at").notNull(),
});
