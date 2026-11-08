CREATE TABLE `commands` (
	`key` text PRIMARY KEY NOT NULL,
	`room_id` text NOT NULL,
	`id` text NOT NULL,
	`action` text NOT NULL,
	`text` text NOT NULL,
	`payload_hash` text NOT NULL,
	`created_at` integer NOT NULL,
	`expires_at` integer NOT NULL,
	`result_status` text,
	`result_code` integer,
	FOREIGN KEY (`room_id`) REFERENCES `rooms`(`id`) ON UPDATE no action ON DELETE cascade
);
--> statement-breakpoint
CREATE INDEX `commands_pending` ON `commands` (`room_id`,`expires_at`);--> statement-breakpoint
CREATE TABLE `rate_limits` (
	`key` text PRIMARY KEY NOT NULL,
	`count` integer NOT NULL,
	`expires_at` integer NOT NULL
);
--> statement-breakpoint
CREATE TABLE `rooms` (
	`id` text PRIMARY KEY NOT NULL,
	`receiver_hash` text NOT NULL,
	`device_hash` text,
	`device_id` text,
	`pair_code` text NOT NULL,
	`pair_nonce` text NOT NULL,
	`pair_expires` integer NOT NULL,
	`last_seen` integer NOT NULL,
	`cleanup_at` integer NOT NULL,
	`created_at` integer NOT NULL
);
--> statement-breakpoint
CREATE UNIQUE INDEX `rooms_pair_code` ON `rooms` (`pair_code`);