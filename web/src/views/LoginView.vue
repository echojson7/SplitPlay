<!-- SPDX-License-Identifier: MIT -->
<!-- Login with the admin password, or first-time password setup (route meta.setup). -->
<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, ApiError } from '../api/client'
import { errorText } from '../composables'
import { t } from '../i18n'
import { SETUP_SKIPPED_KEY } from '../router'
import { store } from '../store'
import BrandMark from '../components/BrandMark.vue'

const route = useRoute()
const router = useRouter()
const setup = computed(() => route.meta.setup === true)

const password = ref('')
const confirmPassword = ref('')
const error = ref<string | null>(null)
const busy = ref(false)

function target(): string {
  const r = route.query.redirect
  return typeof r === 'string' && r.startsWith('/') && !r.startsWith('/login') && !r.startsWith('/setup') ? r : '/'
}

async function submit(): Promise<void> {
  error.value = null
  if (setup.value) {
    if (password.value.length < 8) return void (error.value = t('setup.tooShort'))
    if (password.value !== confirmPassword.value) return void (error.value = t('setup.mismatch'))
  }
  busy.value = true
  try {
    if (setup.value) {
      await api.setPassword(password.value)
      // Some service builds log in on setup, others need an explicit login; do it if needed.
      const s = await store.refreshSession()
      if (!s.authenticated) await api.login(password.value)
    } else {
      await api.login(password.value)
    }
    await store.refreshSession()
    await router.replace(target())
  } catch (e) {
    error.value = e instanceof ApiError && e.status === 401 ? t('login.wrong') : errorText(e)
  } finally {
    busy.value = false
  }
}

function skip(): void {
  try {
    sessionStorage.setItem(SETUP_SKIPPED_KEY, '1')
  } catch {
    // Not persisted: the setup page shows again on the next navigation.
  }
  void router.replace(target())
}
</script>

<template>
  <section class="login">
    <div class="brand">
      <BrandMark :size="44" />
      <span class="wordmark">SPLITPLAY</span>
    </div>
    <form class="box" novalidate @submit.prevent="submit">
      <h1 class="title">{{ setup ? t('setup.title') : t('login.title') }}</h1>
      <div class="field">
        <label for="pw">{{ setup ? t('setup.password') : t('login.password') }}</label>
        <input
          id="pw"
          v-model="password"
          type="password"
          name="password"
          :autocomplete="setup ? 'new-password' : 'current-password'"
          autofocus
        />
      </div>
      <div v-if="setup" class="field">
        <label for="pw2">{{ t('setup.confirm') }}</label>
        <input id="pw2" v-model="confirmPassword" type="password" name="confirm" autocomplete="new-password" />
      </div>
      <p v-if="error" class="err" role="alert">{{ error }}</p>
      <button type="submit" class="btn-primary submit" :disabled="busy">{{ setup ? t('setup.submit') : t('login.submit') }}</button>
      <button v-if="setup" type="button" class="btn-ghost" @click="skip">{{ t('setup.skip') }}</button>
    </form>
  </section>
</template>

<style scoped>
.login {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 28px;
  padding: 24px 16px;
}

.brand {
  display: flex;
  align-items: center;
  gap: 14px;
  color: #fff;
}

.wordmark {
  font-size: 30px;
  font-weight: 700;
  letter-spacing: 0.12em;
}

.box {
  width: min(400px, 100%);
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 32px;
  border-radius: 4px;
  background: linear-gradient(180deg, rgb(42 71 94 / 70%) 0%, rgb(23 26 33 / 85%) 100%);
  box-shadow: 0 20px 50px rgb(0 0 0 / 45%);
}

.title {
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--accent);
}

.err {
  margin: 0;
}

.submit {
  height: 44px;
  font-size: 15px;
  margin-top: 6px;
}
</style>
