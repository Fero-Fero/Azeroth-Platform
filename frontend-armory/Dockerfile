FROM node:22-bookworm-slim

WORKDIR /usr/app

# Build context is this directory (frontend-armory/), built into the shared
# "azeroth-platform-armory" image and run once per stack by the platform.
COPY package*.json ./
RUN npm install

COPY . ./
RUN npm run build

EXPOSE 48733
CMD [ "npm", "start" ]
