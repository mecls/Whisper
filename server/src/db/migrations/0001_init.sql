create table users (
  id text primary key,
  name text not null,
  created_at integer not null,
  disabled_at integer
);
create table device_tokens (
  id text primary key,
  user_id text not null references users(id),
  token_hash text not null,
  label text not null,
  created_at integer not null,
  last_used_at integer,
  revoked_at integer
);
create unique index device_tokens_hash on device_tokens(token_hash);
create table dictations (
  id text primary key,
  user_id text not null references users(id),
  client_id text not null,
  created_at integer not null,
  raw text not null,
  cleaned text,
  injected text,
  mode text not null,
  language_setting text not null,
  language_detected text,
  app_bundle_id text,
  app_name text,
  audio_ms integer,
  asr_ms integer,
  llm_ms integer,
  asr_model text,
  llm_model text,
  client_version text,
  fallback_reason text
);
create unique index dictations_user_client on dictations(user_id, client_id);
create index dictations_user_created on dictations(user_id, created_at desc);
create table user_settings (
  user_id text primary key references users(id),
  json text not null,
  updated_at integer not null
);
create table dictionary_entries (
  id text primary key,
  user_id text references users(id),
  term text not null,
  replacement text,
  note text,
  created_at integer not null
);
create index dictionary_user on dictionary_entries(user_id);
