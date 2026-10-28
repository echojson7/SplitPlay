<!-- SPDX-License-Identifier: MIT -->
<!-- Add-desk wizard: Windows user, password, resolution and FPS. Emits the new desk config; the parent creates it. -->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { DeskConfig, HostUser } from '../api/types'
import { validateDeskConfig } from '../api/validate'
import { FPS_PRESETS, newDeskConfig, RESOLUTION_PRESETS, resolutionKey } from '../desk'
import { t } from '../i18n'
import ModalDialog from './ModalDialog.vue'

const props = withDefaults(
  defineProps<{
    open: boolean
    /** Local accounts from GET /host/users; empty = unknown, the name is typed. */
    users?: HostUser[]
    /** Desks that exist: their ids and users are taken. */
    desks?: ReadonlyArray<{ id: string; user: string }>
    busy?: boolean
    error?: string | null
  }>(),
  { users: () => [], desks: () => [], busy: false, error: null },
)
const emit = defineEmits<{ close: []; create: [config: DeskConfig] }>()

const NEW = '\u0000new'
const step = ref(0)
const pick = ref<string | null>(null)
const newName = ref('')
const password = ref('')
const res = ref(resolutionKey(1920, 1080))
const customW = ref(1920)
const customH = ref(1080)
const fps = ref(60)
const tried = ref(false)

function reset(): void {
  step.value = 0
  pick.value = props.users.length === 0 ? NEW : null
  newName.value = ''
  password.value = ''
  res.value = resolutionKey(1920, 1080)
  customW.value = 1920
  customH.value = 1080
  fps.value = 60
  tried.value = false
}
watch(
  () => props.open,
  (open) => {
    if (open) reset()
  },
  { immediate: true },
)

const usedUsers = computed(() => new Set(props.desks.map((d) => d.user.toLowerCase())))
function findUser(name: string): HostUser | undefined {
  const n = name.trim().toLowerCase()
  return props.users.find((u) => u.name.toLowerCase() === n)
}
function userBlocked(u: HostUser): boolean {
  return u.isConsoleUser || usedUsers.value.has(u.name.toLowerCase())
}

/** The chosen account name. */
const userName = computed(() => (pick.value === NEW ? newName.value.trim() : (pick.value ?? '')))
/** The chosen account exists on the PC (then its password is required). */
const existing = computed(() => findUser(userName.value))

const width = computed(() => (res.value === 'custom' ? Number(customW.value) : Number(res.value.split('x')[0])))
const height = computed(() => (res.value === 'custom' ? Number(customH.value) : Number(res.value.split('x')[1])))

const config = computed(() =>
  newDeskConfig({
    user: userName.value,
    fullName: existing.value?.fullName,
    password: password.value,
    width: width.value,
    height: height.value,
    fps: fps.value,
    existingIds: props.desks.map((d) => d.id),
  }),
)
const errors = computed(() => validateDeskConfig(config.value))

const userError = computed(() => {
  if (!userName.value) return pick.value === NEW ? t('form.err.user') : null
  if (errors.value.user) return t(errors.value.user)
  const u = existing.value
  if (u && userBlocked(u)) return u.isConsoleUser ? t('wizard.console') : t('wizard.taken')
  if (usedUsers.value.has(userName.value.toLowerCase())) return t('wizard.taken')
  return null
})
const passwordError = computed(() => (existing.value && password.value === '' ? t('form.err.passwordRequired') : null))
const displayError = computed(() => {
  const key = errors.value.resolution ?? errors.value.fps
  return key ? t(key) : null
})

const stepValid = computed(() => {
  if (step.value === 0) return userName.value !== '' && userError.value === null
  if (step.value === 1) return passwordError.value === null
  return displayError.value === null
})

const STEPS = ['wizard.stepUser', 'wizard.stepPassword', 'wizard.stepDisplay'] as const

function next(): void {
  tried.value = true
  if (!stepValid.value || props.busy) return
  tried.value = false
  if (step.value < 2) step.value++
  else emit('create', config.value)
}

function back(): void {
  tried.value = false
  if (step.value > 0) step.value--
}

function choose(name: string): void {
  pick.value = name
  tried.value = false
}
</script>

<template>
  <ModalDialog :open="open" :title="t('wizard.title')" :width="540" @close="emit('close')">
    <p class="step-of">{{ t('wizard.stepOf', { n: step + 1, total: STEPS.length }) }} · {{ t(STEPS[step]!) }}</p>

    <form class="wizard" novalidate @submit.prevent="next">
      <!-- 1. Windows user -->
      <section v-if="step === 0" class="pane" data-step="user">
        <h3>{{ t('wizard.userTitle') }}</h3>
        <div class="radios" role="radiogroup" :aria-label="t('desk.user')">
          <button
            v-for="u in users"
            :key="u.name"
            type="button"
            role="radio"
            :aria-checked="pick === u.name"
            :class="['radio', { checked: pick === u.name }]"
            :data-user="u.name"
            :disabled="userBlocked(u)"
            @click="choose(u.name)"
          >
            <span class="dot" />
            <span class="label">
              <span>{{ u.name }}</span>
              <span v-if="u.isConsoleUser" class="sub">{{ t('wizard.console') }}</span>
              <span v-else-if="usedUsers.has(u.name.toLowerCase())" class="sub">{{ t('wizard.taken') }}</span>
              <span v-else-if="u.fullName" class="sub">{{ u.fullName }}</span>
            </span>
          </button>
          <button
            v-if="users.length > 0"
            type="button"
            role="radio"
            :aria-checked="pick === NEW"
            :class="['radio', { checked: pick === NEW }]"
            data-user-new
            @click="choose(NEW)"
          >
            <span class="dot" />
            <span class="label"><span>{{ t('wizard.newUser') }}</span></span>
          </button>
        </div>
        <div v-if="pick === NEW" class="field indent">
          <label for="wiz-user" :class="{ 'sr-only': users.length > 0 }">{{ users.length ? t('wizard.newUser') : t('desk.user') }}</label>
          <input
            id="wiz-user"
            v-model="newName"
            name="newUser"
            maxlength="20"
            autocomplete="off"
            spellcheck="false"
            :placeholder="t('wizard.newUserPlaceholder')"
            :aria-invalid="tried && !!userError"
          />
        </div>
        <p v-if="tried && (userError || !userName)" class="err" role="alert">{{ userError ?? t('wizard.pickUser') }}</p>
      </section>

      <!-- 2. Password -->
      <section v-else-if="step === 1" class="pane" data-step="password">
        <h3>{{ t('wizard.passwordTitle') }}</h3>
        <p class="muted">{{ userName }}<template v-if="existing?.fullName"> · {{ existing.fullName }}</template></p>
        <div class="field">
          <label for="wiz-password">{{ t('adv.password') }}</label>
          <input
            id="wiz-password"
            v-model="password"
            name="password"
            type="password"
            maxlength="256"
            autocomplete="off"
            autofocus
            :aria-invalid="tried && !!passwordError"
          />
          <span v-if="tried && passwordError" class="err" role="alert">{{ passwordError }}</span>
          <span v-else class="hint">{{ existing ? t('wizard.passwordHint') : t('wizard.passwordNewHint') }}</span>
        </div>
      </section>

      <!-- 3. Resolution and FPS -->
      <section v-else class="pane" data-step="display">
        <h3>{{ t('wizard.resolution') }}</h3>
        <div class="radios cols" role="radiogroup" :aria-label="t('wizard.resolution')">
          <button
            v-for="p in RESOLUTION_PRESETS"
            :key="resolutionKey(p.width, p.height)"
            type="button"
            role="radio"
            :aria-checked="res === resolutionKey(p.width, p.height)"
            :class="['radio', { checked: res === resolutionKey(p.width, p.height) }]"
            :data-res="resolutionKey(p.width, p.height)"
            @click="res = resolutionKey(p.width, p.height)"
          >
            <span class="dot" />
            <span class="label">
              <span class="num">{{ p.width }}×{{ p.height }}</span>
              <span class="sub">{{ p.tag }}</span>
            </span>
          </button>
          <button
            type="button"
            role="radio"
            :aria-checked="res === 'custom'"
            :class="['radio', { checked: res === 'custom' }]"
            data-res="custom"
            @click="res = 'custom'"
          >
            <span class="dot" />
            <span class="label"><span>{{ t('desk.custom') }}</span></span>
          </button>
        </div>
        <div v-if="res === 'custom'" class="custom indent">
          <input v-model.number="customW" name="width" type="number" min="320" max="7680" :aria-label="t('desk.width')" />
          <span class="muted">×</span>
          <input v-model.number="customH" name="height" type="number" min="240" max="4320" :aria-label="t('desk.height')" />
        </div>

        <h3 class="fps-title">{{ t('wizard.fps') }}</h3>
        <div class="radios inline" role="radiogroup" :aria-label="t('wizard.fps')">
          <button
            v-for="f in FPS_PRESETS"
            :key="f"
            type="button"
            role="radio"
            :aria-checked="fps === f"
            :class="['radio', { checked: fps === f }]"
            :data-fps="f"
            @click="fps = f"
          >
            <span class="dot" />
            <span class="label"><span class="num">{{ f }}</span></span>
          </button>
        </div>
        <p v-if="tried && displayError" class="err" role="alert">{{ displayError }}</p>
      </section>

      <p v-if="error" class="inline-msg" role="alert">{{ error }}</p>

      <div class="dlg-foot">
        <button v-if="step > 0" type="button" data-back @click="back">{{ t('common.back') }}</button>
        <button v-else type="button" @click="emit('close')">{{ t('common.cancel') }}</button>
        <button v-if="step < 2" type="submit" class="btn-accent" data-next>{{ t('common.next') }}</button>
        <button v-else type="submit" class="btn-accent" data-create :disabled="busy">{{ t('wizard.create') }}</button>
      </div>
    </form>
  </ModalDialog>
</template>

<style scoped>
.step-of {
  margin: -8px 0 16px;
  font-size: 12px;
  line-height: 16px;
  color: var(--text-2);
}

.wizard {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.pane {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 220px;
}

.pane h3 {
  font-size: 14px;
  font-weight: 600;
}

.fps-title {
  margin-top: 8px;
}

/* RadioButtons. */
.radios {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.radios.cols {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  column-gap: 16px;
}

.radios.inline {
  flex-direction: row;
  flex-wrap: wrap;
  gap: 4px 24px;
}

.radio {
  justify-content: flex-start;
  align-items: flex-start;
  gap: 12px;
  height: auto;
  min-height: 32px;
  padding: 6px 4px;
  background: transparent;
  border: 0;
  text-align: left;
  white-space: normal;
  color: var(--text);
}

.radio:hover:not(:disabled),
.radio:active:not(:disabled) {
  background: transparent;
  color: var(--text);
}

.dot {
  position: relative;
  flex: none;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  border: 1px solid var(--input-bottom);
  background: var(--ctrl);
}

.radio:hover:not(:disabled) .dot {
  background: var(--ctrl-hover);
}

.radio.checked .dot {
  border-color: var(--accent);
  background: var(--accent);
}

.radio.checked .dot::after {
  content: '';
  position: absolute;
  inset: 4px;
  border-radius: 50%;
  background: var(--on-accent);
}

.radio:disabled {
  color: var(--disabled);
}

.radio:disabled .dot {
  border-color: var(--disabled);
  background: transparent;
}

.label {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.sub {
  font-size: 12px;
  line-height: 16px;
  color: var(--text-2);
}

.radio:disabled .sub {
  color: var(--disabled);
}

.num {
  font-variant-numeric: tabular-nums;
}

.indent {
  padding-left: 36px;
}

.custom {
  display: flex;
  align-items: center;
  gap: 8px;
}

.custom input {
  width: 100px;
}

.inline-msg {
  margin: 0;
}

.dlg-foot > button {
  flex: 1;
  max-width: 200px;
}

@media (width <= 560px) {
  .radios.cols {
    grid-template-columns: minmax(0, 1fr);
  }
}
</style>
