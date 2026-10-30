<!-- SPDX-License-Identifier: MIT -->
<!-- The «Advanced» settings of a desk. Builds on the live desk so that quick changes made elsewhere are kept. -->
<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { deskToConfig } from '../api/client'
import { CODECS, GPU_PRIORITIES, type Desk, type DeskConfig, type GpuInfo, type GpuPriority } from '../api/types'
import { hasErrors, validateDeskConfig, type DeskConfigErrors } from '../api/validate'
import { t } from '../i18n'

const props = withDefaults(defineProps<{ desk: Desk; gpus?: GpuInfo[]; running?: boolean; busy?: boolean }>(), {
  gpus: () => [],
  running: false,
  busy: false,
})
const emit = defineEmits<{ save: [config: DeskConfig] }>()

const d = props.desk
const form = reactive({
  name: d.name,
  user: d.user,
  password: '',
  bitrateMbps: d.profile.bitrateKbps / 1000,
  codec: d.profile.codec,
  gpu: d.gpu,
  autostart: d.autostart,
  bluetooth: d.disableBluetoothUserService ?? true,
  steam: d.steamIsolation ?? true,
  hideFromLogon: d.hideFromLogon ?? d.userManaged !== false,
  basePort: String(d.basePort),
  fecPercent: d.profile.fecPercent === undefined ? '' : String(d.profile.fecPercent),
  minFps: d.profile.minFps === undefined ? '' : String(d.profile.minFps),
  gpuPriority: (d.profile.gpuPriority ?? '') as GpuPriority | '',
  // Stored as the worker's `gamepad` setting (keys are case-insensitive); the service defaults it to ds4.
  gamepad: gamepadSetting(d.workerSettings),
  workerSettings: Object.entries(d.workerSettings ?? {})
    .filter(([k]) => k.toLowerCase() !== 'gamepad')
    .map(([k, v]) => `${k}=${v}`)
    .join('\n'),
})
const tried = ref(false)

/** Sunshine's gamepad types; anything else set by hand is kept as its own option. */
function gamepadSetting(ws: Record<string, string> | undefined): string {
  const entry = Object.entries(ws ?? {}).find(([k]) => k.toLowerCase() === 'gamepad')
  return entry ? entry[1].trim().toLowerCase() || 'ds4' : 'ds4'
}
const gamepadOptions = computed(() => {
  const list = [
    { value: 'ds4', label: 'DualShock 4' },
    { value: 'x360', label: 'Xbox 360' },
  ]
  if (!list.some((o) => o.value === form.gamepad)) list.push({ value: form.gamepad, label: form.gamepad })
  return list
})

function parseWorkerSettings(text: string): Record<string, string> {
  const out: Record<string, string> = {}
  for (const line of text.split('\n')) {
    const i = line.indexOf('=')
    if (i <= 0) continue
    const key = line.slice(0, i).trim()
    if (key) out[key] = line.slice(i + 1).trim()
  }
  return out
}

function toConfig(): DeskConfig {
  const base = deskToConfig(props.desk)
  const profile: DeskConfig['profile'] = {
    ...base.profile,
    bitrateKbps: Math.round(Number(form.bitrateMbps) * 1000),
    codec: form.codec,
  }
  if (form.fecPercent.trim() === '') delete profile.fecPercent
  else profile.fecPercent = Number(form.fecPercent)
  if (form.minFps.trim() === '') delete profile.minFps
  else profile.minFps = Number(form.minFps)
  if (form.gpuPriority) profile.gpuPriority = form.gpuPriority
  else delete profile.gpuPriority
  const config: DeskConfig = { ...base, profile }
  // Sent only when changed: an account SplitPlay did not create is hidden only on the owner's explicit request.
  if (form.hideFromLogon !== (props.desk.hideFromLogon ?? props.desk.userManaged !== false))
    config.hideFromLogon = form.hideFromLogon
  if (!props.running) {
    config.name = form.name.trim()
    config.user = form.user.trim()
    config.gpu = form.gpu
    config.autostart = form.autostart
    const port = form.basePort.trim()
    if (port === '') delete config.basePort
    else config.basePort = Number(port)
    // The default (true) is left out unless the desk already had an explicit value.
    if (props.desk.disableBluetoothUserService !== undefined || !form.bluetooth)
      config.disableBluetoothUserService = form.bluetooth
    if (props.desk.steamIsolation !== undefined || !form.steam) config.steamIsolation = form.steam
    const ws = parseWorkerSettings(form.workerSettings)
    for (const k of Object.keys(ws)) if (k.toLowerCase() === 'gamepad') delete ws[k]
    if (form.gamepad !== 'ds4') ws.gamepad = form.gamepad
    if (Object.keys(ws).length > 0) config.workerSettings = ws
    else delete config.workerSettings
    if (form.password !== '') config.password = form.password
  }
  return config
}

const errors = computed<DeskConfigErrors>(() => validateDeskConfig(toConfig()))
function err(field: keyof DeskConfigErrors): string | null {
  const key = errors.value[field]
  return tried.value && key ? t(key) : null
}

const userChanged = computed(() => form.user.trim().toLowerCase() !== props.desk.user.toLowerCase())
const passwordError = computed(() =>
  tried.value && !props.running && userChanged.value && form.password === '' ? t('form.err.passwordRequired') : null,
)

function save(): void {
  tried.value = true
  if (hasErrors(errors.value) || passwordError.value || props.busy) return
  emit('save', toConfig())
  form.password = ''
  tried.value = false
}

// Unknown LUIDs (adapter removed) stay selectable so that saving does not silently change the GPU.
const gpuOptions = computed(() => {
  const list = props.gpus.map((g) => ({ value: g.luid, label: g.name }))
  if (form.gpu !== 'auto' && !list.some((g) => g.value === form.gpu)) list.push({ value: form.gpu, label: form.gpu })
  return list
})
</script>

<template>
  <form class="settings" novalidate @submit.prevent="save">
    <p v-if="running" class="inline-msg info">{{ t('desk.lockedRunning') }}</p>

    <div class="fields">
      <div class="field">
        <label for="adv-name">{{ t('adv.name') }}</label>
        <input id="adv-name" v-model="form.name" name="name" maxlength="64" :disabled="running" :aria-invalid="!!err('name')" />
        <span v-if="err('name')" class="err">{{ err('name') }}</span>
      </div>
      <div class="field">
        <label for="adv-bitrate">{{ t('adv.bitrate') }}</label>
        <input
          id="adv-bitrate"
          v-model.number="form.bitrateMbps"
          name="bitrate"
          type="number"
          min="0.5"
          max="1000"
          step="0.5"
          :aria-invalid="!!err('bitrate')"
        />
        <span v-if="err('bitrate')" class="err">{{ err('bitrate') }}</span>
      </div>
      <div class="field">
        <label for="adv-codec">{{ t('adv.codec') }}</label>
        <select id="adv-codec" v-model="form.codec" name="codec">
          <option v-for="c in CODECS" :key="c" :value="c">{{ c.toUpperCase() }}</option>
        </select>
      </div>
      <div class="field">
        <label for="adv-gamepad">{{ t('adv.gamepad') }}</label>
        <select id="adv-gamepad" v-model="form.gamepad" name="gamepad" :disabled="running">
          <option v-for="o in gamepadOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
        </select>
      </div>
      <div class="field">
        <label for="adv-gpu">{{ t('adv.gpu') }}</label>
        <select id="adv-gpu" v-model="form.gpu" name="gpu" :disabled="running">
          <option value="auto">{{ t('adv.gpuAuto') }}</option>
          <option v-for="g in gpuOptions" :key="g.value" :value="g.value">{{ g.label }}</option>
        </select>
      </div>
      <div class="field">
        <label for="adv-user">{{ t('adv.user') }}</label>
        <input
          id="adv-user"
          v-model="form.user"
          name="user"
          maxlength="20"
          autocomplete="off"
          spellcheck="false"
          :disabled="running"
          :aria-invalid="!!err('user')"
        />
        <span v-if="err('user')" class="err">{{ err('user') }}</span>
      </div>
      <div class="field">
        <label for="adv-password">{{ t('adv.password') }}</label>
        <input
          id="adv-password"
          v-model="form.password"
          name="password"
          type="password"
          maxlength="256"
          autocomplete="off"
          :placeholder="t('adv.passwordKeep')"
          :disabled="running"
          :aria-invalid="!!passwordError"
        />
        <span v-if="passwordError" class="err">{{ passwordError }}</span>
      </div>
      <div class="field">
        <label for="adv-port">{{ t('adv.basePort') }}</label>
        <input
          id="adv-port"
          v-model="form.basePort"
          name="basePort"
          inputmode="numeric"
          :disabled="running"
          :aria-invalid="!!err('basePort')"
        />
        <span v-if="err('basePort')" class="err">{{ err('basePort') }}</span>
      </div>
      <div class="field">
        <label for="adv-fec">{{ t('adv.fec') }}</label>
        <input id="adv-fec" v-model="form.fecPercent" name="fecPercent" inputmode="numeric" :placeholder="t('adv.default')" />
        <span v-if="err('fecPercent')" class="err">{{ err('fecPercent') }}</span>
      </div>
      <div class="field">
        <label for="adv-minfps">{{ t('adv.minFps') }}</label>
        <input id="adv-minfps" v-model="form.minFps" name="minFps" inputmode="numeric" :placeholder="t('adv.default')" />
        <span v-if="err('minFps')" class="err">{{ err('minFps') }}</span>
      </div>
      <div class="field">
        <label for="adv-prio">{{ t('adv.gpuPriority') }}</label>
        <select id="adv-prio" v-model="form.gpuPriority" name="gpuPriority">
          <option value="">{{ t('adv.default') }}</option>
          <option v-for="p in GPU_PRIORITIES" :key="p" :value="p">{{ p }}</option>
        </select>
      </div>
    </div>

    <div class="toggles">
      <label class="switch">
        <input v-model="form.autostart" type="checkbox" name="autostart" :disabled="running" />
        {{ t('adv.autostart') }}
      </label>
      <label class="switch">
        <input v-model="form.bluetooth" type="checkbox" name="bluetooth" :disabled="running" />
        {{ t('adv.bluetooth') }}
      </label>
      <label class="switch">
        <input v-model="form.steam" type="checkbox" name="steam" :disabled="running" />
        {{ t('adv.steam') }}
      </label>
      <label class="switch">
        <input v-model="form.hideFromLogon" type="checkbox" name="hideFromLogon" />
        {{ t('adv.hideFromLogon') }}
      </label>
    </div>

    <div class="field">
      <label for="adv-ws">{{ t('adv.workerSettings') }}</label>
      <textarea
        id="adv-ws"
        v-model="form.workerSettings"
        name="workerSettings"
        rows="3"
        class="mono"
        spellcheck="false"
        :placeholder="t('adv.workerSettingsHint')"
        :disabled="running"
      />
    </div>

    <div class="actions">
      <button type="submit" class="btn-primary" :disabled="busy">{{ t('common.save') }}</button>
    </div>
  </form>
</template>

<style scoped>
.settings {
  display: flex;
  flex-direction: column;
}

.inline-msg {
  margin: 0 0 8px;
}

/* Each setting is a line: label on the left, control on the right, a divider between lines. */
.fields,
.toggles {
  display: flex;
  flex-direction: column;
}

.fields > .field,
.toggles > .switch {
  flex-direction: row;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 4px 16px;
  min-height: 52px;
  padding: 10px 0;
  border-bottom: 1px solid var(--divider);
}

.fields > .field > label {
  flex: 1;
}

.fields > .field > input,
.fields > .field > select {
  width: 240px;
  flex: none;
}

.fields > .field > .err {
  width: 100%;
  text-align: right;
}

.toggles > .switch {
  flex-direction: row-reverse;
  width: 100%;
}

.settings > .field {
  padding: 10px 0;
}

.actions {
  display: flex;
  justify-content: flex-end;
  padding-top: 8px;
}

.actions button {
  min-width: 120px;
}

@media (width <= 600px) {
  .fields > .field > input,
  .fields > .field > select {
    width: 160px;
  }
}
</style>
