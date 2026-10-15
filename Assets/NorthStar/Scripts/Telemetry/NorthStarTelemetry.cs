using UnityEngine;
using System.IO;

namespace NorthStar
{
    public class NorthStarTelemetry : MonoBehaviour
    {
        private float timer = 0f;

        private WheelController wheelController;
        private Transform head;

        private Camera playerCamera;
        private Camera playerDirectorCamera;
        private Camera rightDirectorCamera;
        private Camera leftDirectorCamera;

        private string filePath;

        // Remember previous steering state
        private string previousSteeringState = "";

        void Start()
        {
            // Find steering wheel
            wheelController = FindAnyObjectByType<WheelController>();

            // Find VR player camera
            GameObject headObject = GameObject.Find("CenterEyeAnchor");

            if (headObject != null)
            {
                head = headObject.transform;
                playerCamera = headObject.GetComponent<Camera>();
            }

            // Find right director camera
            GameObject rightCameraObject =
                GameObject.Find("RightDirectorCamera");

            if (rightCameraObject != null)
            {
                rightDirectorCamera =
                    rightCameraObject.GetComponent<Camera>();
                if (rightDirectorCamera != null) rightDirectorCamera.depth = 100;
            }

            // Find left director camera
            GameObject leftCameraObject =
                GameObject.Find("LeftDirectorCamera");

            if (leftCameraObject != null)
            {
                leftDirectorCamera =
                    leftCameraObject.GetComponent<Camera>();
                if (leftDirectorCamera != null) leftDirectorCamera.depth = 100;
            }

            GameObject playerDirectorCameraObject =
                GameObject.Find("PlayerDirectorCamera");

            if (playerDirectorCameraObject != null)
            {
                playerDirectorCamera =
                    playerDirectorCameraObject.GetComponent<Camera>();
                if (playerDirectorCamera != null) playerDirectorCamera.depth = 100;
            }

            // CSV file
            filePath = Path.Combine(
                Application.dataPath,
                "NorthStarTelemetry.csv"
            );

            File.WriteAllText(
                filePath,
                "Time,Heading,Steering,Event,Camera," +
                "BoatX,BoatY,BoatZ," +
                "HeadX,HeadY,HeadZ," +
                "HeadRotX,HeadRotY,HeadRotZ\n"
            );

            Debug.Log("Telemetry + Event Tracking started!");
        }

        void Update()
        {
            float steering = wheelController.Value;

            string steeringState;
            string selectedCamera;

            // Determine current steering state
            if (steering > 0.5f)
            {
                steeringState = "STEERING_RIGHT";
                selectedCamera = "RIGHT_DIRECTOR";
            }
            else if (steering < -0.5f)
            {
                steeringState = "STEERING_LEFT";
                selectedCamera = "LEFT_DIRECTOR";
            }
            else
            {
                steeringState = "STEERING_CENTERED";
                selectedCamera = "PLAYER";
            }

            // Detect when steering state changes
            if (steeringState != previousSteeringState)
            {
                Debug.Log(
                    "EVENT TRACKING: " +
                    steeringState +
                    " | Steering: " + steering
                );

                previousSteeringState = steeringState;
            }

            SetCamera(selectedCamera);

            // TELEMETRY
            timer += Time.deltaTime;

            if (timer >= 1f)
            {
                float heading =
                    BoatController.Instance.HeadingAngle;

                Vector3 boatPosition =
                    BoatController.Instance
                    .MovementSource.CurrentPosition;

                Vector3 headPosition = head.position;
                Vector3 headRotation = head.eulerAngles;

                Debug.Log(
                    "Event: " + steeringState +
                    " | Camera: " + selectedCamera +
                    " | Steering: " + steering +
                    " | Heading: " + heading
                );

                string row =
                    Time.time + "," +
                    heading + "," +
                    steering + "," +
                    steeringState + "," +
                    selectedCamera + "," +
                    boatPosition.x + "," +
                    boatPosition.y + "," +
                    boatPosition.z + "," +
                    headPosition.x + "," +
                    headPosition.y + "," +
                    headPosition.z + "," +
                    headRotation.x + "," +
                    headRotation.y + "," +
                    headRotation.z + "\n";

                File.AppendAllText(filePath, row);

                timer = 0f;
            }
        }

        private void SetCamera1(string cameraName)
        {
            if (rightDirectorCamera != null)
            {
                rightDirectorCamera.enabled =
                    cameraName == "RIGHT_DIRECTOR";
            }

            if (leftDirectorCamera != null)
            {
                leftDirectorCamera.enabled =
                    cameraName == "LEFT_DIRECTOR";
            }

            // Don't touch CenterEyeAnchor.
            // Meta XR controls the player camera.
        }

        // CAMERA SWITCHING
        private void SetCamera(string cameraName)
        {
            if (playerDirectorCamera != null)
            {
                playerDirectorCamera.enabled =
                    cameraName == "PLAYER";
            }

            if (rightDirectorCamera != null)
            {
                rightDirectorCamera.enabled =
                    cameraName == "RIGHT_DIRECTOR";
            }

            if (leftDirectorCamera != null)
            {
                leftDirectorCamera.enabled =
                    cameraName == "LEFT_DIRECTOR";
            }
        }

        // STEERING GRAB EVENT
        public void SteeringGrabbed()
        {
            Debug.Log("EVENT TRACKING: STEERING_GRABBED");
            LogSpecialEvent("STEERING_GRABBED");
        }

        // HARPOON FIRE EVENT
        public void HarpoonFired()
        {
            Debug.Log("EVENT TRACKING: HARPOON_FIRED");
            LogSpecialEvent("HARPOON_FIRED");
        }

        // HARPOON RELOAD EVENT
        public void HarpoonReloaded()
        {
            Debug.Log("EVENT TRACKING: HARPOON_RELOADED");
            LogSpecialEvent("HARPOON_RELOADED");
        }

        // HARPOON REARM EVENT
        public void HarpoonRearmed()
        {
            Debug.Log("EVENT TRACKING: HARPOON_REARMED");
            LogSpecialEvent("HARPOON_REARMED");
        }

        // WRITE SPECIAL EVENTS TO CSV
        private void LogSpecialEvent(string eventName)
        {
            float steering = wheelController.Value;
            float heading =
                BoatController.Instance.HeadingAngle;

            Vector3 boatPosition =
                BoatController.Instance
                .MovementSource.CurrentPosition;

            Vector3 headPosition = head.position;
            Vector3 headRotation = head.eulerAngles;

            string row =
                Time.time + "," +
                heading + "," +
                steering + "," +
                eventName + "," +
                "PLAYER" + "," +
                boatPosition.x + "," +
                boatPosition.y + "," +
                boatPosition.z + "," +
                headPosition.x + "," +
                headPosition.y + "," +
                headPosition.z + "," +
                headRotation.x + "," +
                headRotation.y + "," +
                headRotation.z + "\n";

            File.AppendAllText(filePath, row);
        }
    }
}